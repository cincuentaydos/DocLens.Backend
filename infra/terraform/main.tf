terraform {
  required_version = ">= 1.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }

  # Backend values are intentionally omitted here and passed at init time so
  # sbx and prod can share one state bucket/lock table (created by
  # DocLens.Infra/bootstrap/) under different keys:
  #
  #   terraform init \
  #     -backend-config="bucket=doclens-terraform-state-<ACCOUNT_ID>" \
  #     -backend-config="key=lambda-processing/<ENV>/terraform.tfstate" \
  #     -backend-config="region=eu-west-1" \
  #     -backend-config="dynamodb_table=doclens-terraform-locks" \
  #     -backend-config="encrypt=true"
  #
  # <ACCOUNT_ID> and the bucket name come from `terraform output` in
  # DocLens.Infra/bootstrap/ (run once per account, see that repo). <ENV> is
  # "sbx" or "prod".
  backend "s3" {}
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = "DocLens"
      Environment = var.environment
      ManagedBy   = "terraform"
    }
  }
}

data "aws_caller_identity" "current" {}

locals {
  # Same deterministic name DocLens.Infra/bootstrap/main.tf gives the shared
  # state bucket.
  state_bucket = "doclens-terraform-state-${data.aws_caller_identity.current.account_id}"
}

# Reads DocLens.Infra's outputs for the platform resources this module
# needs (document bucket, Aurora cluster, Cognito) — see
# DocLens.Infra/README.md "Cross-repo wiring". DocLens.Infra must already
# be applied for this environment before this can succeed.
data "terraform_remote_state" "infra" {
  backend = "s3"

  config = {
    bucket = local.state_bucket
    key    = "infra/${var.environment}/terraform.tfstate"
    region = var.aws_region
  }
}

module "processing" {
  source              = "./modules/processing"
  environment         = var.environment
  document_bucket_arn = data.terraform_remote_state.infra.outputs.document_bucket_arn
  document_bucket_id  = data.terraform_remote_state.infra.outputs.document_bucket_id
  aurora_cluster_arn  = data.terraform_remote_state.infra.outputs.aurora_cluster_arn
  aurora_secret_arn   = data.terraform_remote_state.infra.outputs.aurora_secret_arn
  user_pool_client_id = data.terraform_remote_state.infra.outputs.user_pool_client_id
  user_pool_endpoint  = data.terraform_remote_state.infra.outputs.user_pool_endpoint
  kms_key_arn         = data.terraform_remote_state.infra.outputs.kms_key_arn
  lambda_zip_path     = var.lambda_zip_path
  embedding_model_id  = var.embedding_model_id
  chat_model_id       = var.chat_model_id
  cors_allow_origins  = var.cors_allow_origins
  enable_swagger      = var.enable_swagger
}
