# All three functions publish from the same ZIP (var.lambda_zip_path) — see
# the note on variables.tf. `provided.al2023` is used instead of a "dotnet10"
# runtime enum value because the pinned hashicorp/aws ~> 5.x provider does
# not yet list it (see ADR-002 / iac-comparison.md).

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
      DOCUMENT_BUCKET   = var.document_bucket_id
      AURORA_CLUSTER    = var.aurora_cluster_arn
      AURORA_SECRET     = var.aurora_secret_arn
      COGNITO_ENDPOINT  = var.user_pool_endpoint
      CHAT_MODEL_ID     = var.chat_model_id
      KNOWLEDGE_BASE_ID = var.knowledge_base_id
    }
  }

  depends_on = [aws_iam_role_policy.api]
}

# --- ADR-011 revision (2026-09-28): the 3-Step-Functions pipeline ---
# The four functions below, action-routed per workflow instead of one Lambda
# per state, replace the old doclens-processor-{env}/doclens-ocr-result-{env}
# pair (removed 2026-09-29's cleanup pass — see infra-deployment-log.md).

resource "aws_lambda_function" "extraction_worker" {
  function_name = "doclens-extraction-worker-${var.environment}"
  role          = aws_iam_role.extraction_worker.arn
  handler       = var.extraction_worker_handler
  runtime       = "provided.al2023"
  s3_bucket     = aws_s3_bucket.artifacts.id
  s3_key        = aws_s3_object.lambda_zip.key
  memory_size   = 1024
  timeout       = 120 # single action per invocation, not the whole workflow — StartTextractJob returns immediately, it doesn't poll

  source_code_hash = filebase64sha256(var.lambda_zip_path)

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_group  = aws_cloudwatch_log_group.extraction_worker.name
    log_format = "JSON"
  }

  environment {
    variables = {
      DOCUMENT_BUCKET        = var.document_bucket_id
      KB_SOURCE_BUCKET       = var.kb_source_bucket_id
      AURORA_CLUSTER         = var.aurora_cluster_arn
      AURORA_SECRET          = var.aurora_secret_arn
      CHAT_MODEL_ID          = var.chat_model_id
      TEXTRACT_SNS_TOPIC_ARN = aws_sns_topic.textract_completion.arn
      TEXTRACT_SNS_ROLE_ARN  = aws_iam_role.textract_sns_publish.arn
    }
  }

  depends_on = [aws_iam_role_policy.extraction_worker]
}

resource "aws_lambda_function" "textract_bridge" {
  function_name = "doclens-textract-bridge-${var.environment}"
  role          = aws_iam_role.textract_bridge.arn
  handler       = var.textract_bridge_handler
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
    log_group  = aws_cloudwatch_log_group.textract_bridge.name
    log_format = "JSON"
  }

  depends_on = [aws_iam_role_policy.textract_bridge]
}

resource "aws_sns_topic_subscription" "textract_bridge" {
  topic_arn = aws_sns_topic.textract_completion.arn
  protocol  = "lambda"
  endpoint  = aws_lambda_function.textract_bridge.arn
}

resource "aws_lambda_permission" "textract_bridge_sns" {
  statement_id  = "AllowSNS"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.textract_bridge.function_name
  principal     = "sns.amazonaws.com"
  source_arn    = aws_sns_topic.textract_completion.arn
}

resource "aws_lambda_function" "review_worker" {
  function_name = "doclens-review-worker-${var.environment}"
  role          = aws_iam_role.review_worker.arn
  handler       = var.review_worker_handler
  runtime       = "provided.al2023"
  s3_bucket     = aws_s3_bucket.artifacts.id
  s3_key        = aws_s3_object.lambda_zip.key
  memory_size   = 256
  timeout       = 30

  source_code_hash = filebase64sha256(var.lambda_zip_path)

  tracing_config {
    mode = "Active"
  }

  logging_config {
    log_group  = aws_cloudwatch_log_group.review_worker.name
    log_format = "JSON"
  }

  environment {
    variables = {
      AURORA_CLUSTER = var.aurora_cluster_arn
      AURORA_SECRET  = var.aurora_secret_arn
    }
  }

  depends_on = [aws_iam_role_policy.review_worker]
}

resource "aws_lambda_function" "sync_worker" {
  function_name = "doclens-sync-worker-${var.environment}"
  role          = aws_iam_role.sync_worker.arn
  handler       = var.sync_worker_handler
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
    log_group  = aws_cloudwatch_log_group.sync_worker.name
    log_format = "JSON"
  }

  environment {
    variables = {
      KB_SOURCE_BUCKET  = var.kb_source_bucket_id
      KNOWLEDGE_BASE_ID = var.knowledge_base_id
      DATA_SOURCE_ID    = var.knowledge_base_data_source_id
      AURORA_CLUSTER    = var.aurora_cluster_arn
      AURORA_SECRET     = var.aurora_secret_arn
    }
  }

  depends_on = [aws_iam_role_policy.sync_worker]
}
