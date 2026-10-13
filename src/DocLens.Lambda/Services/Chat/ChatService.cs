using System.Text;
using System.Text.Json;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using DocLens.Lambda.Context;
using DocLens.Lambda.Models.Responses;

namespace DocLens.Lambda.Services.Chat;

public class ChatService : IChatService
{
    private const string Database = "doclens";

    private readonly IAmazonBedrockAgentRuntime _bedrockAgentRuntime;
    private readonly IAmazonBedrockRuntime _bedrockRuntime;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<ChatService> _logger;
    private readonly string _knowledgeBaseId;
    private readonly string _modelId;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public ChatService(
        IAmazonBedrockAgentRuntime bedrockAgentRuntime,
        IAmazonBedrockRuntime bedrockRuntime,
        IAmazonRDSDataService rdsData,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<ChatService> logger)
    {
        _bedrockAgentRuntime = bedrockAgentRuntime;
        _bedrockRuntime = bedrockRuntime;
        _rdsData = rdsData;
        _tenantContext = tenantContext;
        _logger = logger;
        _knowledgeBaseId = configuration["KNOWLEDGE_BASE_ID"]
            ?? throw new InvalidOperationException("KNOWLEDGE_BASE_ID is not set.");
        _modelId = configuration["CHAT_MODEL_ID"]
            ?? throw new InvalidOperationException("CHAT_MODEL_ID is not set.");
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    public async Task<ChatResponse> AskAsync(string pregunta, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId;

        var retrieval = await _bedrockAgentRuntime.RetrieveAsync(new RetrieveRequest
        {
            KnowledgeBaseId = _knowledgeBaseId,
            RetrievalQuery = new KnowledgeBaseQuery { Text = pregunta },
            RetrievalConfiguration = new KnowledgeBaseRetrievalConfiguration
            {
                VectorSearchConfiguration = new KnowledgeBaseVectorSearchConfiguration
                {
                    // empresa_id is the only isolation enforced here — see
                    // the "not ADR-001-complete" note on IChatService.
                    Filter = new Amazon.BedrockAgentRuntime.Model.RetrievalFilter
                    {
                        Equals = new FilterAttribute { Key = "empresa_id", Value = tenantId }
                    }
                }
            }
        }, cancellationToken);

        var chunks = retrieval.RetrievalResults;
        var documentoIds = chunks
            .Select(r => TryExtractDocumentoId(r.Location?.S3Location?.Uri))
            .Where(id => id is not null)
            .Distinct()
            .Cast<string>()
            .ToList();

        var context = string.Join("\n\n---\n\n", chunks.Select(r => r.Content?.Text ?? ""));

        var rawAnswer = await GenerateAsync(pregunta, context, cancellationToken);

        var piiMap = await LoadPiiMappingsAsync(documentoIds, cancellationToken);
        var answer = SubstitutePlaceholders(rawAnswer, piiMap);

        _logger.LogInformation(
            "Chat answered for tenant {TenantId}, {ChunkCount} chunks from {DocumentCount} documents",
            tenantId, chunks.Count, documentoIds.Count);

        return new ChatResponse
        {
            Respuesta = answer,
            Fuentes = documentoIds
        };
    }

    private async Task<string> GenerateAsync(string pregunta, string context, CancellationToken cancellationToken)
    {
        var prompt = $"""
            Answer the question using only the context below. If the context doesn't
            contain the answer, say so — never invent information not present in it.

            Context:
            {context}

            Question: {pregunta}
            """;

        var requestBody = JsonSerializer.Serialize(new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = 1024,
            messages = new[] { new { role = "user", content = prompt } }
        });

        var response = await _bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody))
        }, cancellationToken);

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    // Redacted content keys follow {tenantId}/documents/{documentoId}/v{n}.txt
    // (Services/Pii's write convention) — the KB's S3 location URI carries it.
    private static string? TryExtractDocumentoId(string? s3Uri)
    {
        if (string.IsNullOrEmpty(s3Uri)) return null;
        var parts = s3Uri.Split('/');
        var idx = Array.IndexOf(parts, "documents");
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : null;
    }

    private async Task<Dictionary<string, string>> LoadPiiMappingsAsync(
        IReadOnlyList<string> documentoIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>();
        if (documentoIds.Count == 0) return map;

        foreach (var documentoId in documentoIds)
        {
            var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                ResourceArn = _auroraClusterArn,
                SecretArn = _auroraSecretArn,
                Database = Database,
                Sql = "SELECT placeholder, valor_real FROM pii_mappings WHERE documento_id = :documentoId::uuid",
                Parameters = [new SqlParameter { Name = "documentoId", Value = new Field { StringValue = documentoId } }]
            }, cancellationToken);

            foreach (var record in response.Records)
            {
                map[record[0].StringValue!] = record[1].StringValue!;
            }
        }

        return map;
    }

    private static string SubstitutePlaceholders(string text, Dictionary<string, string> piiMap)
    {
        foreach (var (placeholder, valorReal) in piiMap)
        {
            text = text.Replace(placeholder, valorReal);
        }
        return text;
    }
}
