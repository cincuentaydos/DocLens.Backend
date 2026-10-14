using Amazon.Comprehend;
using Amazon.Comprehend.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace DocLens.Lambda.Services.Pii;

public class ComprehendPiiRedactionService : IPiiRedactionService
{
    private const string Database = "doclens";

    // DetectPiiEntities caps input at 100 KiB UTF-8 — a real multi-page
    // legal document can exceed that. Chunking + offset-remapping across
    // chunks is real work this pass doesn't do (ADR-014's open question on
    // Comprehend vs. a Bedrock prompt is still open too); documents beyond
    // this size currently only get PII-redacted within the first chunk.
    // Flagged here, not silently swallowed.
    private const int MaxBytes = 100_000;

    private static readonly Dictionary<string, string> PlaceholderPrefixByType = new()
    {
        ["NAME"] = "PARTE",
        ["ADDRESS"] = "DIRECCION",
        ["DATE_TIME"] = "FECHA",
        ["EMAIL"] = "EMAIL",
        ["PHONE"] = "TELEFONO",
        ["BANK_ACCOUNT_NUMBER"] = "CUENTA",
        ["CREDIT_DEBIT_NUMBER"] = "TARJETA",
    };

    private const double MinConfidence = 0.5;

    // Spanish company tax ID (CIF): an entity-type letter, 7 digits and a
    // control digit or letter. Comprehend tends to tag it as
    // BANK_ACCOUNT_NUMBER, but it identifies a legal person, not a natural
    // one — out of GDPR's scope and useful context for the KB. Personal
    // IDs stay redacted: DNI starts with a digit and NIE with X/Y/Z, and
    // K/L/M (other personal NIFs) are deliberately left out of this class.
    private static readonly System.Text.RegularExpressions.Regex CompanyTaxIdPattern =
        new(@"^[ABCDEFGHJNPQRSUVW]\d{7}[0-9A-J]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IAmazonComprehend _comprehend;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly ILogger<ComprehendPiiRedactionService> _logger;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public ComprehendPiiRedactionService(
        IAmazonComprehend comprehend,
        IAmazonRDSDataService rdsData,
        IConfiguration configuration,
        ILogger<ComprehendPiiRedactionService> logger)
    {
        _comprehend = comprehend;
        _rdsData = rdsData;
        _logger = logger;
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    public async Task<PiiRedactionResult> RedactAsync(
        string documentoId, string rawText, CancellationToken cancellationToken = default)
    {
        var textToScan = TruncateToByteLimit(rawText, MaxBytes);
        if (textToScan.Length < rawText.Length)
        {
            _logger.LogWarning(
                "Document {DocumentoId} text ({OriginalLength} chars) exceeds Comprehend's DetectPiiEntities limit — only the first {ScannedLength} chars were scanned for PII",
                documentoId, rawText.Length, textToScan.Length);
        }

        var response = await _comprehend.DetectPiiEntitiesAsync(new DetectPiiEntitiesRequest
        {
            Text = textToScan,
            LanguageCode = LanguageCode.Es
        }, cancellationToken);

        var entities = response.Entities
            .Where(e => e.Score >= MinConfidence)
            .Where(e => !IsCompanyTaxId(textToScan, e))
            .OrderByDescending(e => e.BeginOffset) // reverse order so earlier offsets stay valid while replacing
            .ToList();

        // Two passes on purpose: placeholder letters/numbers must be
        // assigned in reading order (the first name mentioned is PARTE_A,
        // whichever offset Comprehend happened to list it at) — but the
        // actual StringBuilder replacement below still has to run in
        // reverse-offset order, or replacing an earlier entity shifts the
        // offsets of every entity after it.
        var placeholderByValue = new Dictionary<string, string>();
        var counterByPrefix = new Dictionary<string, int>();
        var mappings = new List<(string Placeholder, string ValorReal, string TipoEntidad)>();

        foreach (var entity in entities.OrderBy(e => e.BeginOffset))
        {
            if (entity.BeginOffset is null || entity.EndOffset is null) continue;

            var begin = entity.BeginOffset.Value;
            var end = entity.EndOffset.Value;
            if (begin < 0 || end > textToScan.Length || begin >= end) continue;

            var value = textToScan[begin..end];
            if (placeholderByValue.ContainsKey(value)) continue;

            var typeName = entity.Type?.Value ?? "OTRO";
            var prefix = PlaceholderPrefixByType.GetValueOrDefault(typeName, "DATO");
            var n = counterByPrefix.GetValueOrDefault(prefix, 0) + 1;
            counterByPrefix[prefix] = n;

            // NAME gets letters (PARTE_A, PARTE_B...) to read naturally in
            // legal text ("la parte A"); everything else gets numbers.
            var placeholder = prefix == "PARTE"
                ? $"[{prefix}_{(char)('A' + n - 1)}]"
                : $"[{prefix}_{n}]";

            placeholderByValue[value] = placeholder;
            mappings.Add((placeholder, value, typeName));
        }

        var redacted = new System.Text.StringBuilder(textToScan);

        foreach (var entity in entities)
        {
            if (entity.BeginOffset is null || entity.EndOffset is null) continue;

            var begin = entity.BeginOffset.Value;
            var end = entity.EndOffset.Value;
            if (begin < 0 || end > redacted.Length || begin >= end) continue;

            var value = redacted.ToString(begin, end - begin);
            if (!placeholderByValue.TryGetValue(value, out var placeholder)) continue;

            redacted.Remove(begin, end - begin);
            redacted.Insert(begin, placeholder);
        }

        if (mappings.Count > 0)
        {
            await InsertMappingsAsync(documentoId, mappings, cancellationToken);
        }

        _logger.LogInformation(
            "Redacted {EntityCount} PII entities ({UniqueCount} unique) for documento {DocumentoId}",
            entities.Count, mappings.Count, documentoId);

        return new PiiRedactionResult(redacted.ToString(), mappings.Count);
    }

    private async Task InsertMappingsAsync(
        string documentoId,
        IReadOnlyList<(string Placeholder, string ValorReal, string TipoEntidad)> mappings,
        CancellationToken cancellationToken)
    {
        var request = new BatchExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "INSERT INTO pii_mappings (documento_id, placeholder, valor_real, tipo_entidad) VALUES (:documentoId::uuid, :placeholder, :valorReal, :tipoEntidad)",
            ParameterSets = mappings.Select(m => new List<SqlParameter>
            {
                new() { Name = "documentoId", Value = new Field { StringValue = documentoId } },
                new() { Name = "placeholder", Value = new Field { StringValue = m.Placeholder } },
                new() { Name = "valorReal", Value = new Field { StringValue = m.ValorReal } },
                new() { Name = "tipoEntidad", Value = new Field { StringValue = m.TipoEntidad } }
            }).ToList()
        };

        await _rdsData.BatchExecuteStatementAsync(request, cancellationToken);
    }

    private static bool IsCompanyTaxId(string text, PiiEntity entity)
    {
        if (entity.BeginOffset is not { } begin || entity.EndOffset is not { } end) return false;
        if (begin < 0 || end > text.Length || begin >= end) return false;

        var value = text[begin..end].Replace(" ", "").Replace("-", "").ToUpperInvariant();
        return CompanyTaxIdPattern.IsMatch(value);
    }

    private static string TruncateToByteLimit(string text, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        // Binary-search-free approximate cut: UTF-8 is at most 4 bytes/char,
        // so maxBytes chars is always a safe upper bound to start trimming from.
        var cut = Math.Min(text.Length, maxBytes);
        while (cut > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > maxBytes)
        {
            cut--;
        }
        return text[..cut];
    }
}
