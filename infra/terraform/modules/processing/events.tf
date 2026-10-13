# Custom events DocLens itself emits to chain the three Step Functions
# (ADR-011 revision, 2026-09-28) — source "doclens.processing", one
# detail-type per transition. Each EventBridge rule here follows the same
# shape as guardduty_event.tf's: a rule, a role EventBridge assumes to call
# states:StartExecution, and a small DLQ-only queue for the target's
# dead_letter_config — no consumer queue, unlike the old processing/
# ocr_result queues in queues.tf. This is the "native EventBridge retry
# instead of an SQS buffer" simplification noted in ADR-011's revision.

# --- shared: EventBridge → Extraction (guardduty_event.tf's rule + this one) ---

resource "aws_iam_role" "eventbridge_start_extraction" {
  name = "doclens-eventbridge-start-extraction-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "eventbridge_start_extraction" {
  name = "doclens-eventbridge-start-extraction-${var.environment}"
  role = aws_iam_role.eventbridge_start_extraction.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "StartExecution"
      Effect   = "Allow"
      Action   = ["states:StartExecution"]
      Resource = aws_sfn_state_machine.document_extraction.arn
    }]
  })
}

resource "aws_sqs_queue" "extraction_trigger_dlq" {
  name                      = "doclens-extraction-trigger-dlq-${var.environment}"
  message_retention_seconds = 1209600 # 14 days
}

resource "aws_sqs_queue_policy" "extraction_trigger_dlq_allow_eventbridge" {
  queue_url = aws_sqs_queue.extraction_trigger_dlq.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AllowEventBridge"
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sqs:SendMessage"
      Resource  = aws_sqs_queue.extraction_trigger_dlq.arn
    }]
  })
}

# --- reprocess: doclens-api's POST .../reprocess → back into Extraction ---
# Manual only (ADR-011) — NEEDS_CORRECTION never triggers this by itself.

resource "aws_cloudwatch_event_rule" "document_reprocess_requested" {
  name        = "doclens-document-reprocess-${var.environment}"
  description = "doclens-api's POST /documents/{id}/versions/{n}/reprocess re-starts Extraction for the same document — always a human action, never automatic."

  event_pattern = jsonencode({
    source      = ["doclens.processing"]
    detail-type = ["DocumentReprocessRequested"]
  })
}

resource "aws_cloudwatch_event_target" "reprocess_to_extraction" {
  rule     = aws_cloudwatch_event_rule.document_reprocess_requested.name
  arn      = aws_sfn_state_machine.document_extraction.arn
  role_arn = aws_iam_role.eventbridge_start_extraction.arn

  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 3
  }

  dead_letter_config {
    arn = aws_sqs_queue.extraction_trigger_dlq.arn
  }
}

# --- DocumentExtracted → Review ---

resource "aws_iam_role" "eventbridge_start_review" {
  name = "doclens-eventbridge-start-review-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "eventbridge_start_review" {
  name = "doclens-eventbridge-start-review-${var.environment}"
  role = aws_iam_role.eventbridge_start_review.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "StartExecution"
      Effect   = "Allow"
      Action   = ["states:StartExecution"]
      Resource = aws_sfn_state_machine.document_review.arn
    }]
  })
}

resource "aws_sqs_queue" "review_trigger_dlq" {
  name                      = "doclens-review-trigger-dlq-${var.environment}"
  message_retention_seconds = 1209600
}

resource "aws_sqs_queue_policy" "review_trigger_dlq_allow_eventbridge" {
  queue_url = aws_sqs_queue.review_trigger_dlq.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AllowEventBridge"
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sqs:SendMessage"
      Resource  = aws_sqs_queue.review_trigger_dlq.arn
    }]
  })
}

resource "aws_cloudwatch_event_rule" "document_extracted" {
  name        = "doclens-document-extracted-${var.environment}"
  description = "Extraction finished (PENDING_REVIEW) — starts the blocking human review gate."

  event_pattern = jsonencode({
    source      = ["doclens.processing"]
    detail-type = ["DocumentExtracted"]
  })
}

resource "aws_cloudwatch_event_target" "extracted_to_review" {
  rule     = aws_cloudwatch_event_rule.document_extracted.name
  arn      = aws_sfn_state_machine.document_review.arn
  role_arn = aws_iam_role.eventbridge_start_review.arn

  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 3
  }

  dead_letter_config {
    arn = aws_sqs_queue.review_trigger_dlq.arn
  }
}

# --- DocumentApproved → KB Sync ---

resource "aws_iam_role" "eventbridge_start_kb_sync" {
  name = "doclens-eventbridge-start-kb-sync-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "eventbridge_start_kb_sync" {
  name = "doclens-eventbridge-start-kb-sync-${var.environment}"
  role = aws_iam_role.eventbridge_start_kb_sync.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "StartExecution"
      Effect   = "Allow"
      Action   = ["states:StartExecution"]
      Resource = aws_sfn_state_machine.document_kb_sync.arn
    }]
  })
}

resource "aws_sqs_queue" "kb_sync_trigger_dlq" {
  name                      = "doclens-kb-sync-trigger-dlq-${var.environment}"
  message_retention_seconds = 1209600
}

resource "aws_sqs_queue_policy" "kb_sync_trigger_dlq_allow_eventbridge" {
  queue_url = aws_sqs_queue.kb_sync_trigger_dlq.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AllowEventBridge"
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sqs:SendMessage"
      Resource  = aws_sqs_queue.kb_sync_trigger_dlq.arn
    }]
  })
}

resource "aws_cloudwatch_event_rule" "document_approved" {
  name        = "doclens-document-approved-${var.environment}"
  description = "A reviewer approved the extracted fields — starts the Bedrock KB sync."

  event_pattern = jsonencode({
    source      = ["doclens.processing"]
    detail-type = ["DocumentApproved"]
  })
}

resource "aws_cloudwatch_event_target" "approved_to_kb_sync" {
  rule     = aws_cloudwatch_event_rule.document_approved.name
  arn      = aws_sfn_state_machine.document_kb_sync.arn
  role_arn = aws_iam_role.eventbridge_start_kb_sync.arn

  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 3
  }

  dead_letter_config {
    arn = aws_sqs_queue.kb_sync_trigger_dlq.arn
  }
}
