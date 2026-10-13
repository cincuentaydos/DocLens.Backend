# GuardDuty scan-result → EventBridge → Step Functions StartExecution
# (ADR-011, revised 2026-09-28). Only a CLEAN result starts the Extraction
# state machine; the rule simply never matches/forwards anything else, so a
# threat or unscannable file never reaches the extraction worker.
#
# NOTE: the exact event pattern below (source/detail-type/detail field names)
# is a best-effort reconstruction of the GuardDuty Malware Protection for S3
# "object scan result" event — verify it against a real captured event (or
# the current AWS documentation) before relying on it, since this is a
# comparatively new event type and the schema isn't hand-verified here.
#
# The "not clean" path (THREATS_FOUND / UNSCANNABLE → write REJECTED) is an
# open question in ADR-011/ADR-004 and intentionally NOT wired here yet.

resource "aws_cloudwatch_event_rule" "guardduty_clean_scan" {
  name        = "doclens-guardduty-clean-${var.environment}"
  description = "Forwards GuardDuty Malware Protection scan results for the documents bucket to SQS only when the result is clean."

  event_pattern = jsonencode({
    source      = ["aws.guardduty"]
    detail-type = ["GuardDuty Malware Protection Object Scan Result"]
    detail = {
      s3ObjectDetails = {
        bucketName = [var.document_bucket_id]
      }
      scanResultDetails = {
        scanResultStatus = ["NO_THREATS_FOUND"]
      }
    }
  })
}


# --- ADR-011 revision (2026-09-28): StartExecution replaces the SQS target ---
# The old "processing" SQS queue this rule used to feed is now dead weight —
# nothing consumes it once this retarget lands (doclens-processor-{env} is
# still deployed but simply stops receiving messages; see the step-5
# cleanup pass in the implementation plan for its removal). The IAM role +
# retry/DLQ setup here is shared with the reprocess rule in events.tf,
# since both start the same state machine.

resource "aws_cloudwatch_event_target" "guardduty_clean_to_extraction" {
  rule     = aws_cloudwatch_event_rule.guardduty_clean_scan.name
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
