output "api_endpoint" {
  description = "Raw HTTP API invoke URL. Consumed by DocLens.Infra's edge module (CloudFront custom origin) via terraform_remote_state — see that repo's README.md \"Cross-repo wiring\"."
  value       = module.processing.api_endpoint
}

output "api_id" {
  value = module.processing.api_id
}

output "api_lambda_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (lambda:UpdateFunctionCode) via terraform_remote_state."
  value       = module.processing.api_lambda_arn
}

output "extraction_worker_lambda_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (lambda:UpdateFunctionCode) via terraform_remote_state."
  value       = module.processing.extraction_worker_lambda_arn
}

output "textract_bridge_lambda_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (lambda:UpdateFunctionCode) via terraform_remote_state."
  value       = module.processing.textract_bridge_lambda_arn
}

output "review_worker_lambda_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (lambda:UpdateFunctionCode) via terraform_remote_state."
  value       = module.processing.review_worker_lambda_arn
}

output "sync_worker_lambda_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (lambda:UpdateFunctionCode) via terraform_remote_state."
  value       = module.processing.sync_worker_lambda_arn
}

output "artifacts_bucket_arn" {
  description = "Consumed by DocLens.Infra/governance's GitHub Actions deploy role (s3:PutObject, to stage the CI-built package) via terraform_remote_state."
  value       = module.processing.artifacts_bucket_arn
}
