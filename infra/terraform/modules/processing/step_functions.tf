# ADR-011 revision (2026-09-28) — the document pipeline as three Step
# Functions state machines instead of a Lambda+SQS chain. Each Task state
# below invokes ONE of the four action-routed worker Lambdas (see
# lambdas.tf's comment on that decision) with an "action" field the C#
# handler switches on — the Lambda function count stays flat as the
# workflow grows, only the ASL below grows.
#
# NOTE on JSONPath field names: the exact `$.stepName.xxx` references below
# are the intended shape, but haven't been exercised against the real
# worker Lambda payloads yet (those don't exist in code as of this
# writing — see the implementation plan's execution order, Terraform
# lands before the C#). Expect a follow-up pass once the handlers are
# written and this gets smoke-tested end to end.
#
# STANDARD (not EXPRESS) workflow type on all three — an execution can
# legitimately sit paused for hours/days waiting on a human reviewer
# (doclens-document-review-{env}), which EXPRESS's 5-minute ceiling can't
# accommodate.

# --- Extraction ---

resource "aws_cloudwatch_log_group" "sfn_extraction" {
  name              = "/aws/vendedlogs/states/doclens-document-extraction-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "sfn_extraction" {
  name = "doclens-sfn-extraction-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "states.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "sfn_extraction" {
  name = "doclens-sfn-extraction-policy-${var.environment}"
  role = aws_iam_role.sfn_extraction.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "InvokeWorker"
        Effect   = "Allow"
        Action   = ["lambda:InvokeFunction"]
        Resource = aws_lambda_function.extraction_worker.arn
      },
      {
        # EmitDocumentExtracted — native events:putEvents integration, not
        # a Lambda call, so this lives on the state machine's own role.
        Sid      = "EmitEvent"
        Effect   = "Allow"
        Action   = ["events:PutEvents"]
        Resource = "arn:aws:events:*:*:event-bus/default"
      },
      {
        Sid    = "Logging"
        Effect = "Allow"
        Action = [
          "logs:CreateLogDelivery", "logs:GetLogDelivery", "logs:UpdateLogDelivery",
          "logs:DeleteLogDelivery", "logs:ListLogDeliveries", "logs:PutResourcePolicy",
          "logs:DescribeResourcePolicies", "logs:DescribeLogGroups"
        ]
        Resource = "*"
      },
      {
        # tracing_configuration below — X-Ray doesn't support resource-level
        # permissions, hence "*".
        Sid    = "XRay"
        Effect = "Allow"
        Action = [
          "xray:PutTraceSegments", "xray:PutTelemetryRecords",
          "xray:GetSamplingRules", "xray:GetSamplingTargets"
        ]
        Resource = "*"
      }
    ]
  })
}

resource "aws_sfn_state_machine" "document_extraction" {
  name     = "doclens-document-extraction-${var.environment}"
  role_arn = aws_iam_role.sfn_extraction.arn
  type     = "STANDARD"

  # One X-Ray trace per execution, linked to the worker Lambdas' own
  # (Active) traces — the pipeline shows up end to end in the trace map.
  tracing_configuration {
    enabled = true
  }

  logging_configuration {
    log_destination        = "${aws_cloudwatch_log_group.sfn_extraction.arn}:*"
    include_execution_data = true
    level                  = "ALL"
  }

  definition = jsonencode({
    Comment = "ADR-011 — detect format, OCR if needed, classify, extract fields, redact PII."
    StartAt = "DetectFormat"
    States = {
      DetectFormat = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.extraction_worker.arn
          Payload = {
            "action"  = "DetectFormat"
            "input.$" = "$"
          }
        }
        ResultPath = "$.detectFormat"
        Next       = "NeedsOcr"
      }
      NeedsOcr = {
        Type = "Choice"
        Choices = [{
          Variable      = "$.detectFormat.Payload.needsOcr"
          BooleanEquals = true
          Next          = "StartTextractJob"
        }]
        Default = "ClassifyDocument"
      }
      StartTextractJob = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke.waitForTaskToken"
        Parameters = {
          FunctionName = aws_lambda_function.extraction_worker.arn
          Payload = {
            "action"      = "StartTextractJob"
            "taskToken.$" = "$$.Task.Token"
            "input.$"     = "$"
          }
        }
        ResultPath = "$.ocr"
        Next       = "ClassifyDocument"
      }
      ClassifyDocument = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.extraction_worker.arn
          Payload = {
            "action"  = "ClassifyDocument"
            "input.$" = "$"
          }
        }
        ResultPath = "$.classify"
        Next       = "SemanticExtraction"
      }
      SemanticExtraction = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.extraction_worker.arn
          Payload = {
            "action"  = "SemanticExtraction"
            "input.$" = "$"
          }
        }
        ResultPath = "$.semantic"
        Next       = "DetectAndRedactPII"
      }
      DetectAndRedactPII = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.extraction_worker.arn
          Payload = {
            "action"  = "DetectAndRedactPII"
            "input.$" = "$"
          }
        }
        ResultPath = "$.redacted"
        Next       = "EmitDocumentExtracted"
      }
      EmitDocumentExtracted = {
        Type     = "Task"
        Resource = "arn:aws:states:::events:putEvents"
        Parameters = {
          Entries = [{
            Source       = "doclens.processing"
            DetailType   = "DocumentExtracted"
            EventBusName = "default"
            "Detail.$"   = "$.redacted.Payload"
          }]
        }
        End = true
      }
    }
  })
}

# --- Human review ---

resource "aws_cloudwatch_log_group" "sfn_review" {
  name              = "/aws/vendedlogs/states/doclens-document-review-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "sfn_review" {
  name = "doclens-sfn-review-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "states.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "sfn_review" {
  name = "doclens-sfn-review-policy-${var.environment}"
  role = aws_iam_role.sfn_review.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "InvokeWorker"
        Effect   = "Allow"
        Action   = ["lambda:InvokeFunction"]
        Resource = aws_lambda_function.review_worker.arn
      },
      {
        Sid      = "EmitEvent"
        Effect   = "Allow"
        Action   = ["events:PutEvents"]
        Resource = "arn:aws:events:*:*:event-bus/default"
      },
      {
        Sid    = "Logging"
        Effect = "Allow"
        Action = [
          "logs:CreateLogDelivery", "logs:GetLogDelivery", "logs:UpdateLogDelivery",
          "logs:DeleteLogDelivery", "logs:ListLogDeliveries", "logs:PutResourcePolicy",
          "logs:DescribeResourcePolicies", "logs:DescribeLogGroups"
        ]
        Resource = "*"
      },
      {
        # tracing_configuration below — X-Ray doesn't support resource-level
        # permissions, hence "*".
        Sid    = "XRay"
        Effect = "Allow"
        Action = [
          "xray:PutTraceSegments", "xray:PutTelemetryRecords",
          "xray:GetSamplingRules", "xray:GetSamplingTargets"
        ]
        Resource = "*"
      }
    ]
  })
}

resource "aws_sfn_state_machine" "document_review" {
  name     = "doclens-document-review-${var.environment}"
  role_arn = aws_iam_role.sfn_review.arn
  type     = "STANDARD"

  # One X-Ray trace per execution, linked to the worker Lambdas' own
  # (Active) traces — the pipeline shows up end to end in the trace map.
  tracing_configuration {
    enabled = true
  }

  logging_configuration {
    log_destination        = "${aws_cloudwatch_log_group.sfn_review.arn}:*"
    include_execution_data = true
    level                  = "ALL"
  }

  definition = jsonencode({
    Comment = "ADR-011 — blocking human review gate before KB sync. Resolved by doclens-api's POST /review (SendTaskSuccess/Failure), never automatically."
    StartAt = "EnterReview"
    States = {
      EnterReview = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke.waitForTaskToken"
        Parameters = {
          FunctionName = aws_lambda_function.review_worker.arn
          Payload = {
            "action"      = "EnterReview"
            "taskToken.$" = "$$.Task.Token"
            "input.$"     = "$"
          }
        }
        ResultPath = "$.review"
        Next       = "Approved"
      }
      Approved = {
        Type = "Choice"
        Choices = [{
          Variable      = "$.review.approved"
          BooleanEquals = true
          Next          = "WriteApproved"
        }]
        Default = "WriteNeedsCorrection"
      }
      WriteApproved = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.review_worker.arn
          Payload = {
            "action"  = "WriteApproved"
            "input.$" = "$"
          }
        }
        ResultPath = "$.writeApproved"
        Next       = "EmitDocumentApproved"
      }
      EmitDocumentApproved = {
        Type     = "Task"
        Resource = "arn:aws:states:::events:putEvents"
        Parameters = {
          Entries = [{
            Source       = "doclens.processing"
            DetailType   = "DocumentApproved"
            EventBusName = "default"
            # {tenantId, documentoId, versionNumero} — what WriteApproved's
            # Lambda returns, NOT $.review (the approve/reject decision
            # itself, which Sync has no use for).
            "Detail.$" = "$.writeApproved.Payload"
          }]
        }
        End = true
      }
      # NEEDS_CORRECTION is a dead end on purpose — no automatic retry.
      # POST /documents/{id}/versions/{n}/reprocess (manual) is what starts
      # a fresh Extraction execution afterwards (see events.tf).
      WriteNeedsCorrection = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.review_worker.arn
          Payload = {
            "action"  = "WriteNeedsCorrection"
            "input.$" = "$"
          }
        }
        End = true
      }
    }
  })
}

# --- KB sync ---

resource "aws_cloudwatch_log_group" "sfn_kb_sync" {
  name              = "/aws/vendedlogs/states/doclens-document-kb-sync-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "sfn_kb_sync" {
  name = "doclens-sfn-kb-sync-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "states.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "sfn_kb_sync" {
  name = "doclens-sfn-kb-sync-policy-${var.environment}"
  role = aws_iam_role.sfn_kb_sync.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "InvokeWorker"
        Effect   = "Allow"
        Action   = ["lambda:InvokeFunction"]
        Resource = aws_lambda_function.sync_worker.arn
      },
      {
        Sid    = "Logging"
        Effect = "Allow"
        Action = [
          "logs:CreateLogDelivery", "logs:GetLogDelivery", "logs:UpdateLogDelivery",
          "logs:DeleteLogDelivery", "logs:ListLogDeliveries", "logs:PutResourcePolicy",
          "logs:DescribeResourcePolicies", "logs:DescribeLogGroups"
        ]
        Resource = "*"
      },
      {
        # tracing_configuration below — X-Ray doesn't support resource-level
        # permissions, hence "*".
        Sid    = "XRay"
        Effect = "Allow"
        Action = [
          "xray:PutTraceSegments", "xray:PutTelemetryRecords",
          "xray:GetSamplingRules", "xray:GetSamplingTargets"
        ]
        Resource = "*"
      }
    ]
  })
}

resource "aws_sfn_state_machine" "document_kb_sync" {
  name     = "doclens-document-kb-sync-${var.environment}"
  role_arn = aws_iam_role.sfn_kb_sync.arn
  type     = "STANDARD"

  # One X-Ray trace per execution, linked to the worker Lambdas' own
  # (Active) traces — the pipeline shows up end to end in the trace map.
  tracing_configuration {
    enabled = true
  }

  logging_configuration {
    log_destination        = "${aws_cloudwatch_log_group.sfn_kb_sync.arn}:*"
    include_execution_data = true
    level                  = "ALL"
  }

  definition = jsonencode({
    Comment = "ADR-011 — Bedrock KB StartIngestionJob has no native completion event (verified 2026-09-28), so this polls via Wait+GetIngestionJob."
    StartAt = "PrepareContent"
    States = {
      PrepareContent = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.sync_worker.arn
          Payload = {
            "action"  = "PrepareContent"
            "input.$" = "$"
          }
        }
        ResultPath = "$.prepare"
        Next       = "StartIngestionJob"
      }
      StartIngestionJob = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.sync_worker.arn
          Payload = {
            "action"  = "StartIngestionJob"
            "input.$" = "$"
          }
        }
        ResultPath = "$.ingestion"
        Next       = "WaitForIngestion"
      }
      WaitForIngestion = {
        Type    = "Wait"
        Seconds = 30
        Next    = "GetIngestionJob"
      }
      GetIngestionJob = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.sync_worker.arn
          Payload = {
            "action"  = "GetIngestionJob"
            "input.$" = "$"
          }
        }
        ResultPath = "$.ingestionStatus"
        Next       = "IngestionComplete"
      }
      IngestionComplete = {
        Type = "Choice"
        Choices = [
          {
            Variable     = "$.ingestionStatus.Payload.status"
            StringEquals = "COMPLETE"
            Next         = "WriteIndexed"
          },
          {
            Variable     = "$.ingestionStatus.Payload.status"
            StringEquals = "FAILED"
            Next         = "WriteFailed"
          }
        ]
        Default = "WaitForIngestion"
      }
      WriteIndexed = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.sync_worker.arn
          Payload = {
            "action"  = "WriteIndexed"
            "input.$" = "$"
          }
        }
        End = true
      }
      WriteFailed = {
        Type     = "Task"
        Resource = "arn:aws:states:::lambda:invoke"
        Parameters = {
          FunctionName = aws_lambda_function.sync_worker.arn
          Payload = {
            "action"  = "WriteFailed"
            "input.$" = "$"
          }
        }
        End = true
      }
    }
  })
}
