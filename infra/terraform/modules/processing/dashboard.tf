# One operational dashboard for everything this module deploys: the HTTP
# API, the five Lambdas, the three ADR-011 state machines and their trigger
# DLQs. Same views as a manual CloudWatch check, kept in code so every
# environment gets them. No alarms here yet — this is for looking, not paging.

data "aws_region" "current" {}

locals {
  dashboard_region = data.aws_region.current.name

  dashboard_lambdas = {
    api               = aws_lambda_function.api.function_name
    extraction_worker = aws_lambda_function.extraction_worker.function_name
    textract_bridge   = aws_lambda_function.textract_bridge.function_name
    review_worker     = aws_lambda_function.review_worker.function_name
    sync_worker       = aws_lambda_function.sync_worker.function_name
  }

  dashboard_state_machines = {
    extraction = aws_sfn_state_machine.document_extraction.arn
    review     = aws_sfn_state_machine.document_review.arn
    kb_sync    = aws_sfn_state_machine.document_kb_sync.arn
  }

  dashboard_dlqs = [
    aws_sqs_queue.extraction_trigger_dlq.name,
    aws_sqs_queue.review_trigger_dlq.name,
    aws_sqs_queue.kb_sync_trigger_dlq.name,
  ]

  # One line per Lambda for a given AWS/Lambda metric.
  lambda_metric = {
    for metric in ["Invocations", "Errors", "Throttles", "Duration"] :
    metric => [for name in values(local.dashboard_lambdas) : ["AWS/Lambda", metric, "FunctionName", name]]
  }
}

resource "aws_cloudwatch_dashboard" "main" {
  dashboard_name = "doclens-${var.environment}"

  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "text"
        x      = 0
        y      = 0
        width  = 24
        height = 1
        # Header text and transparent background as edited in the console.
        properties = {
          markdown   = "## DocLens ${var.environment} — API, Lambdas (Extraction → Review → KB Sync) and DLQs"
          background = "transparent"
        }
      },

      # --- API Gateway ---
      {
        type   = "metric"
        x      = 0
        y      = 1
        width  = 12
        height = 6
        properties = {
          title  = "API — requests and errors"
          region = local.dashboard_region
          stat   = "Sum"
          period = 300
          view   = "timeSeries"
          metrics = [
            ["AWS/ApiGateway", "Count", "ApiId", aws_apigatewayv2_api.main.id, { label = "Requests" }],
            ["AWS/ApiGateway", "4xx", "ApiId", aws_apigatewayv2_api.main.id, { label = "4xx" }],
            ["AWS/ApiGateway", "5xx", "ApiId", aws_apigatewayv2_api.main.id, { label = "5xx" }],
          ]
        }
      },
      {
        type   = "metric"
        x      = 12
        y      = 1
        width  = 12
        height = 6
        properties = {
          title  = "API — latency (ms)"
          region = local.dashboard_region
          period = 300
          view   = "timeSeries"
          metrics = [
            ["AWS/ApiGateway", "Latency", "ApiId", aws_apigatewayv2_api.main.id, { stat = "Average", label = "Average" }],
            ["AWS/ApiGateway", "Latency", "ApiId", aws_apigatewayv2_api.main.id, { stat = "p99", label = "p99" }],
          ]
        }
      },

      # --- Lambdas ---
      {
        type   = "metric"
        x      = 0
        y      = 7
        width  = 8
        height = 6
        properties = {
          title   = "Lambda — invocations"
          region  = local.dashboard_region
          stat    = "Sum"
          period  = 300
          view    = "timeSeries"
          metrics = local.lambda_metric["Invocations"]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 7
        width  = 8
        height = 6
        properties = {
          title   = "Lambda — errors"
          region  = local.dashboard_region
          stat    = "Sum"
          period  = 300
          view    = "timeSeries"
          metrics = local.lambda_metric["Errors"]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 7
        width  = 8
        height = 6
        properties = {
          title   = "Lambda — duration p99 (ms)"
          region  = local.dashboard_region
          stat    = "p99"
          period  = 300
          view    = "timeSeries"
          metrics = local.lambda_metric["Duration"]
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 13
        width  = 8
        height = 6
        properties = {
          title   = "Lambda — throttles"
          region  = local.dashboard_region
          stat    = "Sum"
          period  = 300
          view    = "timeSeries"
          metrics = local.lambda_metric["Throttles"]
        }
      },
      {
        # Cold starts only show up in the platform.report log records (JSON
        # log format, see lambdas.tf's logging_config), not as a metric.
        type   = "log"
        x      = 8
        y      = 13
        width  = 16
        height = 6
        properties = {
          title  = "API Lambda — cold starts and memory (per hour)"
          region = local.dashboard_region
          view   = "table"
          query  = <<-EOT
            SOURCE '${aws_cloudwatch_log_group.api.name}'
            | filter type = "platform.report"
            | stats count() as invocations,
                    count(record.metrics.initDurationMs) as cold_starts,
                    avg(record.metrics.initDurationMs) as avg_init_ms,
                    avg(record.metrics.durationMs) as avg_ms,
                    max(record.metrics.maxMemoryUsedMB) as max_memory_mb
              by bin(1h)
            | sort @timestamp desc
          EOT
        }
      },

      # --- Step Functions ---
      {
        type   = "metric"
        x      = 0
        y      = 19
        width  = 12
        height = 6
        properties = {
          title  = "Pipeline — executions started / failed"
          region = local.dashboard_region
          stat   = "Sum"
          period = 300
          view   = "timeSeries"
          metrics = concat(
            [for name, arn in local.dashboard_state_machines : ["AWS/States", "ExecutionsStarted", "StateMachineArn", arn, { label = "${name} started" }]],
            [for name, arn in local.dashboard_state_machines : ["AWS/States", "ExecutionsFailed", "StateMachineArn", arn, { label = "${name} failed" }]],
            [for name, arn in local.dashboard_state_machines : ["AWS/States", "ExecutionsTimedOut", "StateMachineArn", arn, { label = "${name} timed out" }]],
          )
        }
      },
      {
        # Review includes the human wait (.waitForTaskToken), so its time is
        # how long documents sit unreviewed, not processing cost.
        type   = "metric"
        x      = 12
        y      = 19
        width  = 12
        height = 6
        properties = {
          title  = "Pipeline — average execution time (ms)"
          region = local.dashboard_region
          stat   = "Average"
          period = 300
          view   = "timeSeries"
          metrics = [
            for name, arn in local.dashboard_state_machines :
            ["AWS/States", "ExecutionTime", "StateMachineArn", arn, { label = name }]
          ]
        }
      },

      # --- DLQs ---
      {
        # EventBridge-target DLQs: anything here is a pipeline trigger that
        # never started its state machine (manual redrive — see the DLQ
        # runbook in the capstone doc).
        type   = "metric"
        x      = 0
        y      = 25
        width  = 24
        height = 5
        properties = {
          title  = "Trigger DLQs — messages waiting (should be 0)"
          region = local.dashboard_region
          stat   = "Maximum"
          period = 300
          view   = "timeSeries"
          metrics = [
            for queue in local.dashboard_dlqs :
            ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", queue]
          ]
        }
      },
    ]
  })
}
