output "api_endpoint" {
  value = aws_apigatewayv2_stage.default.invoke_url
}

output "api_id" {
  value = aws_apigatewayv2_api.main.id
}

output "api_lambda_arn" {
  value = aws_lambda_function.api.arn
}

output "api_lambda_role_arn" {
  value = aws_iam_role.api.arn
}

output "artifacts_bucket_arn" {
  value = aws_s3_bucket.artifacts.arn
}

output "document_extraction_state_machine_arn" {
  value = aws_sfn_state_machine.document_extraction.arn
}

output "document_review_state_machine_arn" {
  value = aws_sfn_state_machine.document_review.arn
}

output "document_kb_sync_state_machine_arn" {
  value = aws_sfn_state_machine.document_kb_sync.arn
}

output "extraction_worker_lambda_arn" {
  value = aws_lambda_function.extraction_worker.arn
}

output "review_worker_lambda_arn" {
  value = aws_lambda_function.review_worker.arn
}

output "sync_worker_lambda_arn" {
  value = aws_lambda_function.sync_worker.arn
}

output "textract_bridge_lambda_arn" {
  value = aws_lambda_function.textract_bridge.arn
}
