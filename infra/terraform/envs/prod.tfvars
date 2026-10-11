environment = "prod"
aws_region  = "eu-west-1"

# The frontend reaches the API through CloudFront's /api/* behavior (same
# origin), so no CORS origins are needed. Add explicit https:// origins here
# only if another site must call the API directly.
enable_swagger     = false
cors_allow_origins = []
