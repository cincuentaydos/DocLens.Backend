using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using DocLens.Lambda.Models;
using DocLens.Lambda.Options;
using Microsoft.Extensions.Options;

namespace DocLens.Lambda.Services.Classification;

public class BedrockDocumentClassificationService : IDocumentClassificationService
{
    private readonly IAmazonBedrockRuntime _bedrock;
    private readonly string _modelId;
    private readonly ILogger<BedrockDocumentClassificationService> _logger;

    public BedrockDocumentClassificationService(
        IAmazonBedrockRuntime bedrock,
        IOptions<BedrockOptions> options,
        IConfiguration configuration,
        ILogger<BedrockDocumentClassificationService> logger)
    {
        _bedrock = bedrock;
        // Same CHAT_MODEL_ID-wins-over-appsettings rule as
        // BedrockSemanticAnalysisService — the IAM policy only allows this
        // exact model.
        _modelId = configuration["CHAT_MODEL_ID"] ?? options.Value.ModelId;
        _logger = logger;
    }

    public async Task<DocumentType> ClassifyAsync(string rawText, CancellationToken cancellationToken = default)
    {
        var types = string.Join(", ", Enum.GetNames<DocumentType>());
        var prompt = $"""
            Classify this document into exactly one of these types: {types}.
            Return only the type name, nothing else — no punctuation, no explanation.

            Document text:
            {rawText}
            """;

        var requestBody = JsonSerializer.Serialize(new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = 16,
            messages = new[] { new { role = "user", content = prompt } }
        });

        var response = await _bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody))
        }, cancellationToken);

        var raw = ParseText(response.Body).Trim();

        if (!Enum.TryParse<DocumentType>(raw, ignoreCase: true, out var documentType))
        {
            _logger.LogWarning("Bedrock returned an unrecognized document type {Raw} — defaulting to Report", raw);
            documentType = DocumentType.Report;
        }

        _logger.LogInformation("Classified document as {DocumentType}", documentType);

        return documentType;
    }

    private static string ParseText(Stream responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
}
