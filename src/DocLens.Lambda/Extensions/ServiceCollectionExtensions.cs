using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockRuntime;
using Amazon.Comprehend;
using Amazon.EventBridge;
using Amazon.RDSDataService;
using Amazon.S3;
using Amazon.StepFunctions;
using Amazon.Textract;
using DocLens.Lambda.Context;
using DocLens.Lambda.Handlers;
using DocLens.Lambda.Options;
using DocLens.Lambda.Services.Chat;
using DocLens.Lambda.Services.Classification;
using DocLens.Lambda.Services.Ocr;
using DocLens.Lambda.Services.Pii;
using DocLens.Lambda.Services.Review;
using DocLens.Lambda.Services.Semantic;
using DocLens.Lambda.Services.Sync;
using DocLens.Lambda.Services.Upload;

namespace DocLens.Lambda.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocLensServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDefaultAWSOptions(configuration.GetAWSOptions());

        services.AddOptions<BedrockOptions>()
            .Bind(configuration.GetSection(BedrockOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAWSService<IAmazonTextract>();
        services.AddAWSService<IAmazonBedrockRuntime>();
        services.AddAWSService<IAmazonBedrockAgent>();
        services.AddAWSService<IAmazonBedrockAgentRuntime>();
        services.AddAWSService<IAmazonS3>();
        services.AddAWSService<IAmazonRDSDataService>();
        services.AddAWSService<IAmazonComprehend>();
        services.AddAWSService<IAmazonStepFunctions>();
        services.AddAWSService<IAmazonEventBridge>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<IOcrService, TextractOcrService>();
        services.AddScoped<ISemanticAnalysisService, BedrockSemanticAnalysisService>();
        services.AddScoped<IDocumentClassificationService, BedrockDocumentClassificationService>();
        services.AddScoped<IPiiRedactionService, ComprehendPiiRedactionService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IKbSyncService, KbSyncService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IDocumentUploadService, DocumentUploadService>();

        // Non-HTTP Lambda entry points (Step Functions/SNS-triggered) —
        // Program.cs's generic dispatch resolves these by type once it
        // knows which one _HANDLER points at.
        services.AddScoped<ExtractionWorkerHandler>();
        services.AddScoped<TextractBridgeHandler>();
        services.AddScoped<ReviewWorkerHandler>();
        services.AddScoped<SyncWorkerHandler>();

        return services;
    }
}
