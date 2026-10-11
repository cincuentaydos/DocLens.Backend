# A single catch-all route forwards every request to the API Lambda, which
# hosts the full ASP.NET Core minimal API app (Procesos/Clientes/Documentos/
# Usuarios/Permisos-Auditoria/IA — see projects/lambda.md and
# api-reference.md). API Gateway itself doesn't need one route per endpoint;
# routing happens inside the Lambda. Every endpoint in api-reference.md
# requires a Bearer token, so a single JWT-authorized default route covers
# the whole surface — there is no public/anonymous route to carve out.
# Business routes live under /api/* in the app itself (Program.cs), matching
# the path CloudFront forwards unchanged (DocLens.Infra modules/edge).

resource "aws_apigatewayv2_api" "main" {
  name          = "doclens-api-${var.environment}"
  protocol_type = "HTTP"

  # Browsers normally reach the API through CloudFront's /api/* behavior
  # (same origin, no CORS needed). CORS is only for callers hitting the
  # execute-api URL directly; no block at all when the list is empty.
  dynamic "cors_configuration" {
    for_each = length(var.cors_allow_origins) > 0 ? [1] : []
    content {
      allow_origins = var.cors_allow_origins
      allow_methods = ["GET", "POST", "PATCH", "OPTIONS"]
      allow_headers = ["Authorization", "Content-Type"]
    }
  }
}

resource "aws_apigatewayv2_authorizer" "cognito" {
  api_id           = aws_apigatewayv2_api.main.id
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]
  name             = "cognito-jwt"

  jwt_configuration {
    audience = [var.user_pool_client_id]
    issuer   = var.user_pool_endpoint
  }
}

resource "aws_apigatewayv2_integration" "api" {
  api_id                 = aws_apigatewayv2_api.main.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.api.invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "default" {
  api_id             = aws_apigatewayv2_api.main.id
  route_key          = "$default"
  authorization_type = "JWT"
  authorizer_id      = aws_apigatewayv2_authorizer.cognito.id
  target             = "integrations/${aws_apigatewayv2_integration.api.id}"
}

# Swagger UI is the one deliberate exception to "everything requires a
# Bearer token" — it's API documentation, not data, and Swagger UI itself
# lets a caller enter a token to try real (still JWT-protected) requests
# against modules.processing. TenantMiddleware also carves this path out.
resource "aws_apigatewayv2_route" "swagger" {
  api_id             = aws_apigatewayv2_api.main.id
  route_key          = "GET /swagger/{proxy+}"
  authorization_type = "NONE"
  target             = "integrations/${aws_apigatewayv2_integration.api.id}"
}

resource "aws_apigatewayv2_route" "swagger_root" {
  count = var.enable_swagger ? 1 : 0

  api_id             = aws_apigatewayv2_api.main.id
  route_key          = "GET /swagger"
  authorization_type = "NONE"
  target             = "integrations/${aws_apigatewayv2_integration.api.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.main.id
  name        = "$default"
  auto_deploy = true
}

resource "aws_lambda_permission" "api_gateway" {
  statement_id  = "AllowAPIGateway"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.main.execution_arn}/*/*"
}
