variable "environment" {
  type = string
}

variable "document_bucket_arn" {
  type = string
}

variable "document_bucket_id" {
  type = string
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
    below (API, Processor, OCR result) — the same `bootstrap` binary runs in
    all three and picks its role from DOCLENS_FUNCTION_ROLE (lambdas.tf).
    Must be a self-contained linux-x64 publish whose entry binary is named
    `bootstrap` (see infra/README.md). Ignored after the functions exist —
    CI deploys code via UpdateFunctionCode.
  EOT
  type        = string
}

variable "api_handler" {
  description = "Lambda handler string for the API Lambda (ASP.NET Core minimal API host, all Procesos/Clientes/Documentos/Usuarios/IA routes — see projects/lambda.md). Informational only: provided.al2023 always runs `bootstrap`; the role comes from DOCLENS_FUNCTION_ROLE."
  type        = string
  default     = "bootstrap"
}

variable "processor_handler" {
  description = "Lambda handler string for the Processor Lambda (SQS consumer — OCR + semantic extraction, see ADR-011). Informational only: provided.al2023 always runs `bootstrap`; the role comes from DOCLENS_FUNCTION_ROLE."
  type        = string
  default     = "bootstrap"
}

variable "ocr_result_handler" {
  description = "Lambda handler string for the OCR async result Lambda (Textract StartDocumentTextDetection completion via SNS/SQS — see ADR-003). Informational only: provided.al2023 always runs `bootstrap`; the role comes from DOCLENS_FUNCTION_ROLE."
  type        = string
  default     = "bootstrap"
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

variable "cors_allow_origins" {
  description = "Origins allowed by the HTTP API's CORS configuration. Empty disables CORS (CloudFront serves the frontend and /api/* from the same origin)."
  type        = list(string)
  default     = []
}

variable "enable_swagger" {
  description = "Route GET /swagger without a JWT and let the app serve Swagger UI. Must stay false in prod."
  type        = bool
  default     = false
}
