using Amazon.Textract;
using Amazon.BedrockRuntime;
using Amazon.RDSDataService;
using Amazon.S3;
using DocLens.Lambda.Context;
using DocLens.Lambda.Data;
using DocLens.Lambda.Options;
using DocLens.Lambda.Services.Access;
using DocLens.Lambda.Services.Extraction;
using DocLens.Lambda.Services.Ocr;
using DocLens.Lambda.Services.Semantic;
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
        services.AddAWSService<IAmazonS3>();
        services.AddAWSService<IAmazonRDSDataService>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<IDocumentRepository, AuroraDocumentRepository>();
        services.AddScoped<IDocumentAccessGuard, DocumentAccessGuard>();
        services.AddScoped<IOcrService, TextractOcrService>();
        services.AddScoped<ISemanticAnalysisService, BedrockSemanticAnalysisService>();
        services.AddScoped<IDocumentExtractionService, DocumentExtractionService>();
        services.AddScoped<IDocumentUploadService, DocumentUploadService>();

        return services;
    }
}
