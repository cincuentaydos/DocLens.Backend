environment = "sbx"
aws_region  = "eu-west-1"

# Sandbox keeps Swagger and permissive CORS for direct testing against the
# execute-api URL (Bruno, local frontends). Both are rejected in prod by the
# validations in variables.tf.
enable_swagger     = true
cors_allow_origins = ["*"]
