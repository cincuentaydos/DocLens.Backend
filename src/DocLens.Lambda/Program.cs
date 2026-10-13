using Amazon.Lambda.AspNetCoreServer.Hosting;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using DocLens.Lambda.Context;
using DocLens.Lambda.Endpoints;
using DocLens.Lambda.Extensions;
using DocLens.Lambda.Handlers;

// doclens-api-{env} is the only Lambda function here that's an HTTP API
// handler (Amazon.Lambda.AspNetCoreServer.Hosting owns that path). The
// four Step Functions/SNS-triggered workers (ADR-011 revision) share this
// same published ZIP but are NOT ASP.NET Core apps — each function's
// Terraform `handler` string (infra/terraform/modules/processing/lambdas.tf)
// names a Handlers/*.cs class, and this entry point branches on that
// before ever touching WebApplicationBuilder. Without this branch, every
// function here would silently run as the HTTP API host regardless of
// its configured handler — which is exactly the gap this repo had before
// today (doclens-processor-{env}/doclens-ocr-result-{env}'s handler
// strings pointed at classes that were never dispatched to).
var handler = Environment.GetEnvironmentVariable("_HANDLER") ?? "";

if (IsWorkerHandler(handler, out var workerType))
{
    await RunWorkerAsync(workerType);
}
else
{
    RunHttpApi();
}

return;

static bool IsWorkerHandler(string handler, out Type? workerType)
{
    workerType = handler switch
    {
        var h when h.Contains(nameof(ExtractionWorkerHandler)) => typeof(ExtractionWorkerHandler),
        var h when h.Contains(nameof(TextractBridgeHandler)) => typeof(TextractBridgeHandler),
        var h when h.Contains(nameof(ReviewWorkerHandler)) => typeof(ReviewWorkerHandler),
        var h when h.Contains(nameof(SyncWorkerHandler)) => typeof(SyncWorkerHandler),
        _ => null
    };
    return workerType is not null;
}

static async Task RunWorkerAsync(Type? workerType)
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging(b => b.AddJsonConsole());
    services.AddDocLensServices(configuration);
    var provider = services.BuildServiceProvider();

    // Every Handlers/*.cs class exposes the same shape —
    // Task<object> HandleAsync(JsonElement, CancellationToken) — matched
    // via reflection so this dispatcher doesn't grow a case per handler
    // beyond the type-selection switch above.
    var instance = provider.GetRequiredService(workerType!);
    var method = workerType!.GetMethod("HandleAsync")
        ?? throw new InvalidOperationException($"{workerType.Name} has no HandleAsync method.");

    async Task<object> Invoke(System.Text.Json.JsonElement input, ILambdaContext _)
    {
        var task = (Task)method.Invoke(instance, [input, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    // Both type arguments must be explicit: Create<JsonElement> alone lets
    // the compiler bind to an overload whose TOutput isn't the object we
    // actually return, which silently serializes an empty response body
    // (found 2026-09-29 — every worker invocation "succeeded" with a
    // 0-byte payload, breaking every downstream Choice state that reads
    // the output).
    await LambdaBootstrapBuilder.Create<System.Text.Json.JsonElement, object>(Invoke, new DefaultLambdaJsonSerializer())
        .Build()
        .RunAsync();
}

static void RunHttpApi()
{
    var builder = WebApplication.CreateBuilder();

    builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
    builder.Services.AddDocLensServices(builder.Configuration);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseMiddleware<TenantMiddleware>();

    // Only route API Gateway lets through without a JWT (see
    // modules/processing/api_gateway.tf) — everything else still requires a
    // Cognito Bearer token, per ADR-005/api-reference.md.
    app.UseSwagger();
    app.UseSwaggerUI();

    app.MapDocumentEndpoints();
    app.MapChatEndpoints();

    app.Run();
}
