using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using DocLens.Lambda.Models;
using DocLens.Lambda.Options;
using Microsoft.Extensions.Options;

namespace DocLens.Lambda.Services.Semantic;

public class BedrockSemanticAnalysisService : ISemanticAnalysisService
{
    private readonly IAmazonBedrockRuntime _bedrock;
    private readonly BedrockOptions _options;
    private readonly string _modelId;
    private readonly ILogger<BedrockSemanticAnalysisService> _logger;

    public BedrockSemanticAnalysisService(
        IAmazonBedrockRuntime bedrock,
        IOptions<BedrockOptions> options,
        IConfiguration configuration,
        ILogger<BedrockSemanticAnalysisService> logger)
    {
        _bedrock = bedrock;
        _options = options.Value;
        // CHAT_MODEL_ID is the Lambda environment variable Terraform sets
        // (modules/processing/lambdas.tf) — the IAM policy's BedrockGenerate
        // statement only allows invoking that exact model, so this must
        // win over BedrockOptions.ModelId's appsettings.json default or
        // every call gets an AccessDeniedException.
        _modelId = configuration["CHAT_MODEL_ID"] ?? _options.ModelId;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> AnalyzeAsync(
        string rawText,
        DocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting semantic analysis for document type {DocumentType} using model {ModelId}",
            documentType, _modelId);

        var prompt = BuildPrompt(rawText, documentType);

        var requestBody = JsonSerializer.Serialize(new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = _options.MaxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        });

        var response = await _bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody))
        }, cancellationToken);

        return BedrockResponseParser.Parse(response.Body);
    }

    private static string BuildPrompt(string rawText, DocumentType documentType)
    {
        var fields = documentType switch
        {
            DocumentType.Invoice => "issuer, recipient, invoice_number, issue_date, due_date, total_amount, currency, line_items_summary",
            DocumentType.Contract => "parties, effective_date, expiry_date, governing_law, key_obligations_summary",
            DocumentType.Report => "title, author, date, summary, key_findings",
            DocumentType.Cv => "full_name, email, phone, current_role, skills, education_summary, experience_summary",
            _ => "key_fields"
        };

        return $"""
            Extract the following fields from this {documentType} document: {fields}.
            Return a JSON object where each key is a field name and each value is the extracted text.
            If a field is not found, use an empty string as its value.
            Return only the JSON object, no additional text.

            Document text:
            {rawText}
            """;
    }
}
