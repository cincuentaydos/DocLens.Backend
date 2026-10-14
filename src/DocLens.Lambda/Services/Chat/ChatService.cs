using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int NumberOfResults = 5;

    // Placeholders written by ComprehendPiiRedactionService: [PARTE_A], [FECHA_1]...
    private static readonly Regex PlaceholderPattern = new(@"\[([A-Z]+_[A-Z0-9]+)\]", RegexOptions.Compiled);
    // Leftover document tags on bracketed tokens that had no PII mapping.
    private static readonly Regex DocumentTagPattern = new(@"\[D\d+_", RegexOptions.Compiled);
    // Last line of the model's answer: "FUENTES: 1, 3" or "FUENTES: ninguna".
    private static readonly Regex SourcesLinePattern = new(@"\n?\s*FUENTES:\s*(?<ids>[^\n]*)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                    NumberOfResults = NumberOfResults,
                    // empresa_id is the only isolation enforced here — see
                    // the "not ADR-001-complete" note on IChatService.
                    Filter = new Amazon.BedrockAgentRuntime.Model.RetrievalFilter
                    {
                        Equals = new FilterAttribute { Key = "empresa_id", Value = tenantId }
                    }
                }
            }
        }, cancellationToken);

        var chunks = retrieval.RetrievalResults
            .Select(r => (DocumentoId: TryExtractDocumentoId(r.Location?.S3Location?.Uri), Text: r.Content?.Text ?? ""))
            .ToList();

        // Placeholders are only unique within a document — [PARTE_A] in one
        // is a different person from [PARTE_A] in another. Tag each with its
        // document (D1, D2...) before the model sees it, so the answer can be
        // un-redacted with the right document's mappings.
        var tagByDocumentoId = chunks
            .Select(c => c.DocumentoId)
            .Where(id => id is not null)
            .Distinct()
            .Select((id, i) => (Id: id!, Tag: $"D{i + 1}"))
            .ToDictionary(x => x.Id, x => x.Tag);

        var context = string.Join("\n\n---\n\n", chunks.Select((c, i) =>
            $"[{i + 1}]\n{TagPlaceholders(c.Text, c.DocumentoId is null ? null : tagByDocumentoId[c.DocumentoId])}"));

        var rawAnswer = await GenerateAsync(pregunta, context, cancellationToken);
        var (answerText, citedChunks) = SplitSources(rawAnswer, chunks.Count);

        // Only the documents the model actually cited — not every chunk
        // Retrieve returned (it always returns its top N, related or not).
        var fuentes = citedChunks
            .Select(n => chunks[n - 1].DocumentoId)
            .Where(id => id is not null)
            .Distinct()
            .Cast<string>()
            .ToList();

        var piiMap = await LoadPiiMappingsAsync(tagByDocumentoId, cancellationToken);
        var answer = DocumentTagPattern.Replace(SubstitutePlaceholders(answerText, piiMap), "[");

        _logger.LogInformation(
            "Chat answered for tenant {TenantId}, {ChunkCount} chunks from {DocumentCount} documents, {CitedCount} cited",
            tenantId, chunks.Count, tagByDocumentoId.Count, fuentes.Count);

        return new ChatResponse
        {
            Respuesta = answer,
            Fuentes = fuentes
        };
    }

    private async Task<string> GenerateAsync(string pregunta, string context, CancellationToken cancellationToken)
    {
        var prompt = $"""
            Answer the question using only the numbered context fragments below. If
            they don't contain the answer, say so — never invent information not
            present in them. Keep bracketed placeholders such as [D1_PARTE_A]
            exactly as written.

            After the answer, add one last line listing the numbers of the
            fragments you actually used, e.g. "FUENTES: 1, 3" — or "FUENTES: ninguna"
            if none of them answered the question.

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

    private static string TagPlaceholders(string text, string? tag) =>
        tag is null ? text : PlaceholderPattern.Replace(text, m => $"[{tag}_{m.Groups[1].Value}]");

    // Splits off the trailing "FUENTES: ..." line. Returns the 1-based chunk
    // numbers cited, ignoring anything out of range; no line at all means
    // no sources (logged by the caller's count, not guessed).
    private static (string Answer, IReadOnlyList<int> Cited) SplitSources(string rawAnswer, int chunkCount)
    {
        var match = SourcesLinePattern.Match(rawAnswer);
        if (!match.Success)
        {
            return (rawAnswer.Trim(), []);
        }

        var cited = Regex.Matches(match.Groups["ids"].Value, @"\d+")
            .Select(m => int.Parse(m.Value))
            .Where(n => n >= 1 && n <= chunkCount)
            .Distinct()
            .ToList();

        return (rawAnswer[..match.Index].Trim(), cited);
    }

    // Keyed by the document-tagged placeholder ([D1_PARTE_A]), so two
    // documents' [PARTE_A] can never overwrite each other.
    private async Task<Dictionary<string, string>> LoadPiiMappingsAsync(
        IReadOnlyDictionary<string, string> tagByDocumentoId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>();

        foreach (var (documentoId, tag) in tagByDocumentoId)
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
                map[TagPlaceholders(record[0].StringValue!, tag)] = record[1].StringValue!;
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
