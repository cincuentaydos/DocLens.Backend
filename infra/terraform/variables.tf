variable "aws_region" {
  description = "Primary AWS region."
  type        = string
  default     = "eu-west-1"
}

variable "environment" {
  description = "Deployment environment."
  type        = string

  validation {
    condition     = contains(["sbx", "prod"], var.environment)
    error_message = "environment must be sbx or prod."
  }
}

variable "lambda_zip_path" {
  description = <<-EOT
    Path to the Lambda deployment ZIP shared by the API, Processor, and OCR
    result functions (see modules/processing).
    Must be a self-contained linux-x64 publish whose entry binary is named
    `bootstrap` (provided.al2023). Build it with the steps in infra/README.md
    (dotnet publish ... --self-contained + scripts/package-lambda-zip.py).
    Only read when the functions are first created: afterwards CI deploys
    the code and Terraform ignores s3_key/source_code_hash.
  EOT
  type        = string
  default     = "../../src/DocLens.Lambda/bin/Release/net10.0/linux-x64/publish/doclens-lambda.zip"
}

variable "embedding_model_id" {
  description = "Bedrock embedding model (ADR-001). Must match the value DocLens.Infra's knowledge_base module uses — see that repo's README.md \"Cross-repo wiring\"."
  type        = string
  default     = "amazon.titan-embed-text-v2:0"
}

variable "chat_model_id" {
  description = "Bedrock Claude model used for semantic extraction, chat and drafting."
  type        = string
  default     = "eu.anthropic.claude-haiku-4-5-20251001-v1:0"
}

variable "cors_allow_origins" {
  description = "Origins allowed by the HTTP API's CORS configuration. Empty (the default) disables CORS. \"*\" is rejected in prod."
  type        = list(string)
  default     = []

  validation {
    condition     = var.environment != "prod" || !contains(var.cors_allow_origins, "*")
    error_message = "cors_allow_origins must not contain \"*\" in prod."
  }
}

variable "enable_swagger" {
  description = "Expose Swagger UI (unauthenticated GET /swagger). Defaults to false; enabled in envs/sbx.tfvars only."
  type        = bool
  default     = false

  validation {
    condition     = !(var.environment == "prod" && var.enable_swagger)
    error_message = "enable_swagger must be false in prod."
  }
}
