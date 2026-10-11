# All three functions publish from the same ZIP (var.lambda_zip_path) — see
# the note on variables.tf. `provided.al2023` is used instead of a "dotnet10"
# runtime enum value because the pinned hashicorp/aws ~> 5.x provider does
# not yet list it (see ADR-002 / iac-comparison.md).
#
# provided.al2023 always executes the `bootstrap` binary and ignores the
# handler string, so DOCLENS_FUNCTION_ROLE tells that single binary which
# loop to run (src/DocLens.Lambda/Workers/FunctionRole.cs).
#
# Code is deployed by CI (.github/workflows/cd.yml -> UpdateFunctionCode with
# ci/<sha>.zip), so s3_key/source_code_hash are ignored after creation:
# otherwise every `terraform apply` would roll the functions back to whatever
# local ZIP var.lambda_zip_path points at.

resource "aws_lambda_function" "api" {
  function_name = "doclens-api-${var.environment}"
  role          = aws_iam_role.api.arn
  handler       = var.api_handler
  runtime       = "provided.al2023"
  s3_bucket     = aws_s3_bucket.artifacts.id
  s3_key        = aws_s3_object.lambda_zip.key
  memory_size   = 512
  timeout       = 29 # stays under the HTTP API integration ceiling — no long work happens here (ADR-010/011)

  source_code_hash = filebase64sha256(var.lambda_zip_path)

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_group  = aws_cloudwatch_log_group.api.name
    log_format = "JSON"
  }

  environment {
    variables = {
      DOCUMENT_BUCKET        = var.document_bucket_id
      AURORA_CLUSTER         = var.aurora_cluster_arn
      AURORA_SECRET          = var.aurora_secret_arn
      COGNITO_ENDPOINT       = var.user_pool_endpoint
      CHAT_MODEL_ID          = var.chat_model_id
      DOCLENS_FUNCTION_ROLE  = "api"
      DOCLENS_ENABLE_SWAGGER = tostring(var.enable_swagger)
    }
  }

  lifecycle {
    ignore_changes = [s3_key, source_code_hash]
  }

  depends_on = [aws_iam_role_policy.api]
}

resource "aws_lambda_function" "processor" {
  function_name = "doclens-processor-${var.environment}"
  role          = aws_iam_role.processor.arn
  handler       = var.processor_handler
  runtime       = "provided.al2023"
  s3_bucket     = aws_s3_bucket.artifacts.id
  s3_key        = aws_s3_object.lambda_zip.key
  memory_size   = 1024
  timeout       = 300 # accommodates long Textract jobs — ADR-006/011

  source_code_hash = filebase64sha256(var.lambda_zip_path)

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_group  = aws_cloudwatch_log_group.processor.name
    log_format = "JSON"
  }

  environment {
    variables = {
      DOCUMENT_BUCKET        = var.document_bucket_id
      AURORA_CLUSTER         = var.aurora_cluster_arn
      AURORA_SECRET          = var.aurora_secret_arn
      CHAT_MODEL_ID          = var.chat_model_id
      TEXTRACT_SNS_TOPIC_ARN = aws_sns_topic.textract_completion.arn
      TEXTRACT_SNS_ROLE_ARN  = aws_iam_role.textract_sns_publish.arn
      DOCLENS_FUNCTION_ROLE  = "processor"
    }
  }

  lifecycle {
    ignore_changes = [s3_key, source_code_hash]
  }

  depends_on = [aws_iam_role_policy.processor]
}

resource "aws_lambda_event_source_mapping" "processor_sqs" {
  event_source_arn = aws_sqs_queue.processing.arn
  function_name    = aws_lambda_function.processor.arn
  batch_size       = 1

  # The handler reports per-message failures (SQSBatchResponse) instead of
  # failing the whole batch.
  function_response_types = ["ReportBatchItemFailures"]
}

resource "aws_lambda_function" "ocr_result" {
  function_name = "doclens-ocr-result-${var.environment}"
  role          = aws_iam_role.ocr_result.arn
  handler       = var.ocr_result_handler
  runtime       = "provided.al2023"
  s3_bucket     = aws_s3_bucket.artifacts.id
  s3_key        = aws_s3_object.lambda_zip.key
  memory_size   = 256
  timeout       = 60

  source_code_hash = filebase64sha256(var.lambda_zip_path)

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_group  = aws_cloudwatch_log_group.ocr_result.name
    log_format = "JSON"
  }

  environment {
    variables = {
      AURORA_CLUSTER        = var.aurora_cluster_arn
      AURORA_SECRET         = var.aurora_secret_arn
      DOCLENS_FUNCTION_ROLE = "ocr_result"
    }
  }

  lifecycle {
    ignore_changes = [s3_key, source_code_hash]
  }

  depends_on = [aws_iam_role_policy.ocr_result]
}

resource "aws_lambda_event_source_mapping" "ocr_result_sqs" {
  event_source_arn = aws_sqs_queue.ocr_result.arn
  function_name    = aws_lambda_function.ocr_result.arn
  batch_size       = 1

  function_response_types = ["ReportBatchItemFailures"]
}
