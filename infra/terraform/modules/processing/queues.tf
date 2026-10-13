# --- Textract async OCR completion (ADR-003 / architecture.md) ---
# extraction_worker calls StartDocumentTextDetection and returns; Textract
# publishes completion to this SNS topic, which invokes textract_bridge
# directly (protocol="lambda", see lambdas.tf) — no SQS queue in this path
# since the old ocr_result Lambda's queue-based consumption was removed in
# the 2026-09-29 cleanup pass along with that Lambda itself.

resource "aws_sns_topic" "textract_completion" {
  name = "doclens-textract-completion-${var.environment}"
}

# Role Textract assumes to publish the completion notification.
resource "aws_iam_role" "textract_sns_publish" {
  name = "doclens-textract-sns-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "textract.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "textract_sns_publish" {
  name = "doclens-textract-sns-${var.environment}"
  role = aws_iam_role.textract_sns_publish.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "Publish"
      Effect   = "Allow"
      Action   = "sns:Publish"
      Resource = aws_sns_topic.textract_completion.arn
    }]
  })
}
