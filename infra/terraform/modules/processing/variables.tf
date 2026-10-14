variable "environment" {
  type = string
}

variable "document_bucket_arn" {
  type = string
}

variable "document_bucket_id" {
  type = string
}

variable "kb_source_bucket_arn" {
  description = "Redacted-content bucket (ADR-014) — from DocLens.Infra's terraform_remote_state. Never the same as document_bucket_arn."
  type        = string
}

variable "kb_source_bucket_id" {
  type = string
}

variable "knowledge_base_id" {
  description = "Bedrock Knowledge Base ID (from DocLens.Infra's terraform_remote_state) — scopes doclens-sync-worker's StartIngestionJob/GetIngestionJob permission to this one KB."
  type        = string
}

variable "knowledge_base_data_source_id" {
  description = "Bedrock Knowledge Base data source ID — StartIngestionJob needs this alongside knowledge_base_id (it's a request parameter, not part of the IAM resource ARN)."
  type        = string
}

variable "aurora_cluster_arn" {
  type = string
}

variable "aurora_secret_arn" {
  description = "Secrets Manager ARN for the Aurora managed master credentials — needed by both Lambdas to call the RDS Data API."
  type        = string
}

variable "user_pool_client_id" {
  type = string
}

variable "user_pool_endpoint" {
  type = string
}

variable "kms_key_arn" {
  description = "CMK the document bucket and the Aurora master secret are encrypted with (DocLens.Infra's modules/kms) — needed for S3/Secrets Manager access under that key."
  type        = string
}

variable "lambda_zip_path" {
  description = <<-EOT
    Path to the single Lambda deployment ZIP shared by all three functions
    below (API, Processor, OCR result) — each just points at a different
    handler inside the same published .NET assembly.
    Build with: dotnet publish src/DocLens.Lambda -c Release -o /tmp/lambda-out
    then: zip -j /tmp/doclens-lambda.zip /tmp/lambda-out/*
  EOT
  type        = string
}

variable "api_handler" {
  description = "Lambda handler string for the API Lambda (ASP.NET Core minimal API host, all Procesos/Clientes/Documentos/Usuarios/IA routes — see projects/lambda.md)."
  type        = string
  default     = "DocLens.Lambda::DocLens.Lambda.LambdaEntryPoint::FunctionHandlerAsync"
}

variable "extraction_worker_handler" {
  description = "Lambda handler for doclens-extraction-worker — the doclens-document-extraction-{env} Step Functions' single action-routed Lambda (DetectFormat, ClassifyDocument, SemanticExtraction, DetectAndRedactPII, StartTextractJob — ADR-011)."
  type        = string
  default     = "DocLens.Lambda::DocLens.Lambda.Handlers.ExtractionWorkerHandler::HandleAsync"
}

variable "textract_bridge_handler" {
  description = "Lambda handler for doclens-textract-bridge — triggered by the textract_completion SNS topic, resolves the Extraction state machine's StartTextractJob .waitForTaskToken pause via SendTaskSuccess/Failure."
  type        = string
  default     = "DocLens.Lambda::DocLens.Lambda.Handlers.TextractBridgeHandler::HandleAsync"
}

variable "review_worker_handler" {
  description = "Lambda handler for doclens-review-worker — the doclens-document-review-{env} Step Functions' action-routed Lambda (EnterReview, WriteApproved, WriteNeedsCorrection)."
  type        = string
  default     = "DocLens.Lambda::DocLens.Lambda.Handlers.ReviewWorkerHandler::HandleAsync"
}

variable "sync_worker_handler" {
  description = "Lambda handler for doclens-sync-worker — the doclens-document-kb-sync-{env} Step Functions' action-routed Lambda (PrepareContent, StartIngestionJob, GetIngestionJob, WriteIndexed, WriteFailed)."
  type        = string
  default     = "DocLens.Lambda::DocLens.Lambda.Handlers.SyncWorkerHandler::HandleAsync"
}

variable "embedding_model_id" {
  description = "Bedrock embedding model used for RAG (ADR-001)."
  type        = string
  default     = "amazon.titan-embed-text-v2:0"
}

variable "chat_model_id" {
  description = "Bedrock Claude model used for semantic extraction, chat and drafting. A cross-region inference profile ID (region-prefixed, e.g. \"eu.anthropic...\") — several current Claude models reject direct on-demand invocation by bare foundation model ID (\"The provided model identifier is invalid\"). See iam.tf's chat_model_foundation_id local for why both ARN shapes need bedrock:InvokeModel."
  type        = string
  default     = "eu.anthropic.claude-haiku-4-5-20251001-v1:0"
}

# On-demand Bedrock prices used only for the AI dashboard's cost estimate
# (dashboard_ai.tf). Taken from the AWS Price List API for eu-west-1 on
# 2026-10-14 — check https://aws.amazon.com/bedrock/pricing/ before trusting
# them. Claude's are the "Regional" rates, which apply to the eu. (geo)
# cross-region inference profile; a global. profile is ~10% cheaper.
variable "bedrock_price_usd_per_mtok" {
  description = "USD per million tokens for the AI dashboard's cost estimate, keyed by model/direction."
  type = object({
    claude_input    = number
    claude_output   = number
    embedding_input = number
  })
  default = {
    claude_input    = 1.10
    claude_output   = 5.50
    embedding_input = 0.026004
  }
}
