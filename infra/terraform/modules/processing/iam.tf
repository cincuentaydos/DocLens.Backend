# Shared statements every Lambda needs to reach Aurora via the RDS Data API
# (ADR-008) — no VPC networking required.
locals {
  # var.chat_model_id is a cross-region inference profile ID (e.g.
  # "eu.anthropic.claude-haiku-4-5-20251001-v1:0") — some newer Bedrock
  # models don't support direct on-demand invocation by bare foundation
  # model ID at all ("The provided model identifier is invalid"). Invoking
  # through the profile needs bedrock:InvokeModel on BOTH the profile ARN
  # and the underlying foundation model ARN it routes to, so this strips
  # the leading "eu."/"us."/"global." routing prefix to get the latter.
  chat_model_foundation_id = replace(var.chat_model_id, "/^[a-z]+\\./", "")

  aurora_data_api_statements = [
    {
      Sid    = "AuroraDataApi"
      Effect = "Allow"
      Action = [
        "rds-data:ExecuteStatement",
        "rds-data:BatchExecuteStatement",
        "rds-data:BeginTransaction",
        "rds-data:CommitTransaction",
        "rds-data:RollbackTransaction"
      ]
      Resource = var.aurora_cluster_arn
    },
    {
      Sid      = "AuroraSecret"
      Effect   = "Allow"
      Action   = ["secretsmanager:GetSecretValue"]
      Resource = var.aurora_secret_arn
    },
    {
      # Covers both the Aurora secret above and reading document-bucket
      # objects below — both are encrypted under the same CMK (DocLens.Infra's
      # modules/kms), so one kms:Decrypt statement serves every role here.
      Sid      = "KmsDecrypt"
      Effect   = "Allow"
      Action   = ["kms:Decrypt"]
      Resource = var.kms_key_arn
    }
  ]
}

# --- API Lambda role ---

resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/lambda/doclens-api-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "api" {
  name = "doclens-api-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "api" {
  name = "doclens-api-policy-${var.environment}"
  role = aws_iam_role.api.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(local.aurora_data_api_statements, [
      {
        Sid      = "Logs"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.api.arn}:*"
      },
      {
        # POST /documents/prepare only generates the presigned URL — never
        # reads/writes the object body itself (ADR-005).
        Sid      = "S3PresignUpload"
        Effect   = "Allow"
        Action   = ["s3:PutObject"]
        Resource = "${var.document_bucket_arn}/*"
      },
      {
        # GET /documents/{id}/versions/{n} returns the redacted text the KB
        # will ingest (textoRedactado) so the reviewer can check it. Read
        # only; kms:Decrypt comes from aurora_data_api_statements (same CMK).
        Sid      = "S3ReadRedacted"
        Effect   = "Allow"
        Action   = ["s3:GetObject"]
        Resource = "${var.kb_source_bucket_arn}/*"
      },
      {
        # The presigned URL is signed with this role's credentials, so this
        # role (not the browser that eventually PUTs the object) is who
        # needs kms:GenerateDataKey for the bucket's default SSE-KMS to apply.
        Sid      = "S3PresignUploadKms"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey"]
        Resource = var.kms_key_arn
      },
      {
        Sid    = "BedrockGenerate"
        Effect = "Allow"
        Action = ["bedrock:InvokeModel"]
        Resource = [
          "arn:aws:bedrock:*::foundation-model/${local.chat_model_foundation_id}",
          "arn:aws:bedrock:*:*:inference-profile/${var.chat_model_id}"
        ]
      },
      {
        Sid      = "BedrockRetrieve"
        Effect   = "Allow"
        Action   = ["bedrock:Retrieve", "bedrock:RetrieveAndGenerate"]
        Resource = "*" # scoped down once the Knowledge Base ARN exists — see modules/knowledge_base
      },
      {
        # /review resolves the Review state machine's EnterReview wait;
        # /reprocess re-triggers Extraction via a fresh event, never by
        # calling Step Functions directly (see EventBridgePutEvents below) —
        # ADR-011's "no automatic retries" rule for NEEDS_CORRECTION.
        Sid      = "ResolveReviewWait"
        Effect   = "Allow"
        Action   = ["states:SendTaskSuccess", "states:SendTaskFailure"]
        Resource = aws_sfn_state_machine.document_review.arn
      },
      {
        # /reprocess and /chat's audit trail both go through custom events —
        # PrepareUpload etc. keep going straight to Aurora as before.
        Sid      = "EventBridgePutEvents"
        Effect   = "Allow"
        Action   = ["events:PutEvents"]
        Resource = "arn:aws:events:*:*:event-bus/default"
      },
      {
        Sid      = "XRay"
        Effect   = "Allow"
        Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
        Resource = "*"
      }
    ])
  })
}

# --- Extraction worker role (doclens-document-extraction-{env} Step Functions, ADR-011 revision) ---

resource "aws_cloudwatch_log_group" "extraction_worker" {
  name              = "/aws/lambda/doclens-extraction-worker-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "extraction_worker" {
  name = "doclens-extraction-worker-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "extraction_worker" {
  name = "doclens-extraction-worker-policy-${var.environment}"
  role = aws_iam_role.extraction_worker.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(local.aurora_data_api_statements, [
      {
        Sid      = "Logs"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.extraction_worker.arn}:*"
      },
      {
        # DetectFormat/StartTextractJob read the original from document_bucket.
        Sid      = "S3ReadOriginal"
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:GetObjectVersion"]
        Resource = "${var.document_bucket_arn}/*"
      },
      {
        # DetectFormat writes the intermediate raw-extracted text to
        # kb_source (to stay under the 256KB Step Functions state limit);
        # ClassifyDocument/SemanticExtraction read that same object back;
        # DetectAndRedactPII writes the final redacted text there too — so
        # this role needs both directions on kb_source, not just write.
        Sid      = "S3ReadWriteKbSource"
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:PutObject"]
        Resource = "${var.kb_source_bucket_arn}/*"
      },
      {
        # kb_source uses the same CMK as document_bucket — this role only
        # had kms:Decrypt (from aurora_data_api_statements, for reading the
        # original) but never kms:GenerateDataKey, needed for its own writes
        # to kb_source's SSE-KMS default encryption to succeed.
        Sid      = "KmsWriteRedacted"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey"]
        Resource = var.kms_key_arn
      },
      {
        Sid      = "Textract"
        Effect   = "Allow"
        Action   = ["textract:StartDocumentTextDetection"]
        Resource = "*"
      },
      {
        Sid      = "TextractPassRole"
        Effect   = "Allow"
        Action   = ["iam:PassRole"]
        Resource = aws_iam_role.textract_sns_publish.arn
      },
      {
        # DetectAndRedactPII (ADR-014) — Comprehend's PII detection.
        Sid      = "ComprehendPii"
        Effect   = "Allow"
        Action   = ["comprehend:DetectPiiEntities"]
        Resource = "*"
      },
      {
        Sid    = "Bedrock"
        Effect = "Allow"
        Action = ["bedrock:InvokeModel"]
        Resource = [
          "arn:aws:bedrock:*::foundation-model/${local.chat_model_foundation_id}",
          "arn:aws:bedrock:*:*:inference-profile/${var.chat_model_id}"
        ]
      },
      {
        Sid      = "XRay"
        Effect   = "Allow"
        Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
        Resource = "*"
      }
    ])
  })
}

# --- Textract bridge role (SNS-triggered, resolves StartTextractJob's .waitForTaskToken) ---

resource "aws_cloudwatch_log_group" "textract_bridge" {
  name              = "/aws/lambda/doclens-textract-bridge-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "textract_bridge" {
  name = "doclens-textract-bridge-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "textract_bridge" {
  name = "doclens-textract-bridge-policy-${var.environment}"
  role = aws_iam_role.textract_bridge.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "Logs"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.textract_bridge.arn}:*"
      },
      {
        Sid      = "TextractGetResult"
        Effect   = "Allow"
        Action   = ["textract:GetDocumentTextDetection"]
        Resource = "*"
      },
      {
        # The only thing this Lambda exists to do: resume the paused
        # StartTextractJob task in the Extraction state machine.
        Sid      = "ResolveTextractWait"
        Effect   = "Allow"
        Action   = ["states:SendTaskSuccess", "states:SendTaskFailure"]
        Resource = aws_sfn_state_machine.document_extraction.arn
      },
      {
        Sid      = "XRay"
        Effect   = "Allow"
        Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
        Resource = "*"
      }
    ]
  })
}

# --- Review worker role (doclens-document-review-{env} Step Functions) ---

resource "aws_cloudwatch_log_group" "review_worker" {
  name              = "/aws/lambda/doclens-review-worker-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "review_worker" {
  name = "doclens-review-worker-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "review_worker" {
  name = "doclens-review-worker-policy-${var.environment}"
  role = aws_iam_role.review_worker.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(local.aurora_data_api_statements, [
      {
        Sid      = "Logs"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.review_worker.arn}:*"
      },
      {
        Sid      = "XRay"
        Effect   = "Allow"
        Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
        Resource = "*"
      }
    ])
  })
}

# --- Sync worker role (doclens-document-kb-sync-{env} Step Functions) ---

resource "aws_cloudwatch_log_group" "sync_worker" {
  name              = "/aws/lambda/doclens-sync-worker-${var.environment}"
  retention_in_days = 30
}

resource "aws_iam_role" "sync_worker" {
  name = "doclens-sync-worker-role-${var.environment}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "sync_worker" {
  name = "doclens-sync-worker-policy-${var.environment}"
  role = aws_iam_role.sync_worker.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(local.aurora_data_api_statements, [
      {
        Sid      = "Logs"
        Effect   = "Allow"
        Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
        Resource = "${aws_cloudwatch_log_group.sync_worker.arn}:*"
      },
      {
        # PrepareContent's existence/read check — never on document_bucket.
        Sid      = "S3ReadRedacted"
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:HeadObject"]
        Resource = "${var.kb_source_bucket_arn}/*"
      },
      {
        Sid      = "BedrockIngestion"
        Effect   = "Allow"
        Action   = ["bedrock:StartIngestionJob", "bedrock:GetIngestionJob"]
        Resource = "arn:aws:bedrock:*:*:knowledge-base/${var.knowledge_base_id}"
      },
      {
        Sid      = "XRay"
        Effect   = "Allow"
        Action   = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"]
        Resource = "*"
      }
    ])
  })
}
