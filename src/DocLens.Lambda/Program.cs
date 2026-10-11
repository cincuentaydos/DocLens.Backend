using Amazon.Lambda.AspNetCoreServer.Hosting;
using DocLens.Lambda.Context;
using DocLens.Lambda.Endpoints;
using DocLens.Lambda.Extensions;
using DocLens.Lambda.Workers;

// One binary, three Lambdas (see Workers/FunctionRole.cs): the SQS workers
// run their own runtime loop and never start the web host.
var role = FunctionRoleResolver.Resolve(
    Environment.GetEnvironmentVariable(FunctionRoleResolver.RoleVariable),
    Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));

if (role != FunctionRole.Api)
{
    await WorkerHost.RunAsync(role, args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddDocLensServices(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseMiddleware<TenantMiddleware>();

// Swagger is API documentation, not data, but it still maps the attack
// surface: only expose it locally (Development) or where Terraform opts in
// via DOCLENS_ENABLE_SWAGGER (sbx by default, never prod — see
// infra/terraform/variables.tf's enable_swagger). API Gateway only routes
// /swagger without a JWT when that same flag is on.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("DOCLENS_ENABLE_SWAGGER"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Every business route lives under /api so it lines up with CloudFront's
// /api/* behavior (DocLens.Infra modules/edge/cloudfront.tf), which forwards
// the path unchanged to API Gateway.
app.MapGroup("/api").MapDocumentEndpoints();

app.Run();
