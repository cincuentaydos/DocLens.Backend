# AI usage dashboard: Bedrock calls, tokens, latency and quota for the two
# models DocLens uses — Claude (classification, semantic extraction, chat)
# and the Titan embedding model (Knowledge Base ingestion + Retrieve).
#
# AWS/Bedrock only breaks metrics down by ModelId, not by caller, so Claude's
# numbers are classification + extraction + chat combined. The per-feature
# split below comes from the API Lambda's own log lines instead.

locals {
  # Claude is invoked through a cross-region inference profile, and that
  # profile ID is the ModelId dimension. The embedding model is invoked by
  # the Knowledge Base by foundation-model ARN, and that ARN is its ModelId.
  ai_chat_model      = var.chat_model_id
  ai_embedding_model = "arn:aws:bedrock:${local.dashboard_region}::foundation-model/${var.embedding_model_id}"

  ai_models = {
    claude    = local.ai_chat_model
    embedding = local.ai_embedding_model
  }
}

resource "aws_cloudwatch_dashboard" "ai" {
  dashboard_name = "doclens-ai-${var.environment}"

  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "text"
        x      = 0
        y      = 0
        width  = 24
        height = 2
        properties = {
          markdown = <<-EOT
            ## DocLens ${var.environment} — Amazon Bedrock usage
            **Claude** (`${local.ai_chat_model}`): classification, semantic extraction and chat combined. **Embeddings** (`${var.embedding_model_id}`): Knowledge Base ingestion and Retrieve.
          EOT
        }
      },

      # --- Last 24h totals ---
      {
        type   = "metric"
        x      = 0
        y      = 2
        width  = 24
        height = 4
        properties = {
          title                = "Last 24 hours"
          region               = local.dashboard_region
          view                 = "singleValue"
          stat                 = "Sum"
          period               = 86400
          setPeriodToTimeRange = true
          metrics = [
            ["AWS/Bedrock", "Invocations", "ModelId", local.ai_chat_model, { label = "Claude calls" }],
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_chat_model, { label = "Claude input tokens" }],
            ["AWS/Bedrock", "OutputTokenCount", "ModelId", local.ai_chat_model, { label = "Claude output tokens" }],
            ["AWS/Bedrock", "Invocations", "ModelId", local.ai_embedding_model, { label = "Embedding calls" }],
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_embedding_model, { label = "Embedding tokens" }],
          ]
        }
      },

      # --- Cost estimate ---
      {
        # One total (Claude + embeddings). The embedding share isn't shown on
        # its own: it's a fraction of a cent at these volumes, and singleValue
        # renders that in scientific notation (7.56E-5).
        type   = "metric"
        x      = 0
        y      = 6
        width  = 24
        height = 4
        properties = {
          title                = "Estimated Bedrock cost (USD) for the selected time range — Claude + embeddings"
          region               = local.dashboard_region
          view                 = "singleValue"
          stat                 = "Sum"
          period               = 86400
          setPeriodToTimeRange = true
          metrics = [
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_chat_model, { id = "ci", visible = false }],
            ["AWS/Bedrock", "OutputTokenCount", "ModelId", local.ai_chat_model, { id = "co", visible = false }],
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_embedding_model, { id = "ei", visible = false }],
            [{ id = "total", expression = "(FILL(ci, 0) * ${var.bedrock_price_usd_per_mtok.claude_input} + FILL(co, 0) * ${var.bedrock_price_usd_per_mtok.claude_output} + FILL(ei, 0) * ${var.bedrock_price_usd_per_mtok.embedding_input}) / 1000000", label = "Claude + embeddings (estimate)" }],
          ]
        }
      },
      {
        type   = "text"
        x      = 0
        y      = 10
        width  = 24
        height = 1
        properties = {
          markdown = "Prices from the AWS Price List API, eu-west-1, 2026-10-14 — USD per 1M tokens — Claude Haiku 4.5: ${var.bedrock_price_usd_per_mtok.claude_input} input / ${var.bedrock_price_usd_per_mtok.claude_output} output · Titan Embeddings v2: ${var.bedrock_price_usd_per_mtok.embedding_input}. Current prices: [aws.amazon.com/bedrock/pricing](https://aws.amazon.com/bedrock/pricing/)"
        }
      },

      # --- Calls and tokens over time ---
      {
        type   = "metric"
        x      = 0
        y      = 11
        width  = 8
        height = 6
        properties = {
          title  = "Calls per model"
          region = local.dashboard_region
          stat   = "Sum"
          period = 300
          view   = "timeSeries"
          metrics = [
            for name, model in local.ai_models :
            ["AWS/Bedrock", "Invocations", "ModelId", model, { label = name }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 8
        y      = 11
        width  = 8
        height = 6
        properties = {
          title   = "Claude tokens"
          region  = local.dashboard_region
          stat    = "Sum"
          period  = 300
          view    = "timeSeries"
          stacked = true
          metrics = [
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_chat_model, { label = "Input" }],
            ["AWS/Bedrock", "OutputTokenCount", "ModelId", local.ai_chat_model, { label = "Output" }],
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 11
        width  = 8
        height = 6
        properties = {
          title  = "Embedding tokens"
          region = local.dashboard_region
          stat   = "Sum"
          period = 300
          view   = "timeSeries"
          metrics = [
            ["AWS/Bedrock", "InputTokenCount", "ModelId", local.ai_embedding_model, { label = "Input" }],
          ]
        }
      },

      # --- Latency, errors, quota ---
      {
        type   = "metric"
        x      = 0
        y      = 17
        width  = 8
        height = 6
        properties = {
          title  = "Claude latency (ms)"
          region = local.dashboard_region
          period = 300
          view   = "timeSeries"
          metrics = [
            ["AWS/Bedrock", "InvocationLatency", "ModelId", local.ai_chat_model, { stat = "Average", label = "Average" }],
            ["AWS/Bedrock", "InvocationLatency", "ModelId", local.ai_chat_model, { stat = "p99", label = "p99" }],
          ]
        }
      },
      {
        # Bedrock only publishes these once they're non-zero, so "no data"
        # here means no errors, not a broken widget.
        type   = "metric"
        x      = 8
        y      = 17
        width  = 8
        height = 6
        properties = {
          title  = "Errors and throttling (no data = none)"
          region = local.dashboard_region
          stat   = "Sum"
          period = 300
          view   = "timeSeries"
          # concat, not flatten — flatten would also flatten each metric's
          # own [namespace, name, ...] array into loose strings.
          metrics = concat(
            [for name, model in local.ai_models : ["AWS/Bedrock", "InvocationClientErrors", "ModelId", model, { label = "${name} client errors" }]],
            [for name, model in local.ai_models : ["AWS/Bedrock", "InvocationServerErrors", "ModelId", model, { label = "${name} server errors" }]],
            [for name, model in local.ai_models : ["AWS/Bedrock", "InvocationThrottles", "ModelId", model, { label = "${name} throttles" }]],
          )
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 17
        width  = 8
        height = 6
        properties = {
          title  = "Estimated tokens-per-minute quota usage"
          region = local.dashboard_region
          stat   = "Maximum"
          period = 60
          view   = "timeSeries"
          metrics = [
            for name, model in local.ai_models :
            ["AWS/Bedrock", "EstimatedTPMQuotaUsage", "ModelId", model, { label = name }]
          ]
        }
      },

      # --- Per-feature split from logs ---
      {
        # ChatService logs one "Chat answered" line per question; the
        # extraction worker's Bedrock calls are the ClassifyDocument and
        # SemanticExtraction steps of each Extraction execution.
        type   = "log"
        x      = 0
        y      = 23
        width  = 24
        height = 6
        properties = {
          title  = "Chat questions per hour (API Lambda logs)"
          region = local.dashboard_region
          view   = "bar"
          query  = <<-EOT
            SOURCE '${aws_cloudwatch_log_group.api.name}'
            | filter @message like /Chat answered/
            | stats count() as chat_questions by bin(1h)
          EOT
        }
      },
    ]
  })
}
