using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using DocLens.Lambda.Extensions;

namespace DocLens.Lambda.Workers;

// Runs the Lambda runtime loop for the SQS-triggered functions (Processor,
// OCR result) with the same DI registrations the API uses.
public static class WorkerHost
{
    public static async Task RunAsync(FunctionRole role, string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddJsonConsole();
        });
        services.AddDocLensServices(configuration);
        services.AddScoped<ProcessorHandler>();
        services.AddScoped<OcrResultHandler>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);

        Func<SQSEvent, ILambdaContext, Task<SQSBatchResponse>> handler = async (sqsEvent, context) =>
        {
            await using var scope = provider.CreateAsyncScope();
            return role switch
            {
                FunctionRole.Processor => await scope.ServiceProvider.GetRequiredService<ProcessorHandler>().HandleAsync(sqsEvent, context),
                FunctionRole.OcrResult => await scope.ServiceProvider.GetRequiredService<OcrResultHandler>().HandleAsync(sqsEvent, context),
                _ => throw new InvalidOperationException($"{role} is not an SQS worker role.")
            };
        };

        await LambdaBootstrapBuilder
            .Create(handler, new DefaultLambdaJsonSerializer())
            .Build()
            .RunAsync();
    }
}
