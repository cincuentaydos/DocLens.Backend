using System.Text;
using System.Text.Json;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.S3;
using Amazon.S3.Model;
using DocLens.Lambda.Models;
using DocLens.Lambda.Services.Classification;
using DocLens.Lambda.Services.Ocr;
using DocLens.Lambda.Services.Pii;
using DocLens.Lambda.Services.Semantic;
using UglyToad.PdfPig;

namespace DocLens.Lambda.Handlers;

// Single action-routed Lambda behind doclens-document-extraction-{env}
// (ADR-011) — see infra/terraform/modules/processing/step_functions.tf for
// which action each Task state sends. Kept as one function (not one per
// state) per the implementation plan's Lambda-count decision.
public class ExtractionWorkerHandler
{
    // PdfPig-extracted text below this length is treated as "this PDF is
    // scanned, not digital" and falls back to Textract (ADR-003's
    // heuristic — 20-100 chars is the recommended range, 50 chosen).
    private const int PdfPigMinChars = 50;

    private readonly IAmazonS3 _s3;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly IOcrService _ocr;
    private readonly IDocumentClassificationService _classification;
    private readonly ISemanticAnalysisService _semanticAnalysis;
    private readonly IPiiRedactionService _piiRedaction;
    private readonly ILogger<ExtractionWorkerHandler> _logger;
    private readonly string _documentBucket;
    private readonly string _kbSourceBucket;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public ExtractionWorkerHandler(
        IAmazonS3 s3,
        IAmazonRDSDataService rdsData,
        IOcrService ocr,
        IDocumentClassificationService classification,
        ISemanticAnalysisService semanticAnalysis,
        IPiiRedactionService piiRedaction,
        IConfiguration configuration,
        ILogger<ExtractionWorkerHandler> logger)
    {
        _s3 = s3;
        _rdsData = rdsData;
        _ocr = ocr;
        _classification = classification;
        _semanticAnalysis = semanticAnalysis;
        _piiRedaction = piiRedaction;
        _logger = logger;
        _documentBucket = configuration["DOCUMENT_BUCKET"]
            ?? throw new InvalidOperationException("DOCUMENT_BUCKET is not set.");
        _kbSourceBucket = configuration["KB_SOURCE_BUCKET"]
            ?? throw new InvalidOperationException("KB_SOURCE_BUCKET is not set.");
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    public async Task<object> HandleAsync(JsonElement evt, CancellationToken cancellationToken = default)
    {
        var action = evt.GetProperty("action").GetString();
        var input = evt.GetProperty("input");

        _logger.LogInformation("ExtractionWorker handling action {Action}", action);

        return action switch
        {
            "DetectFormat" => await DetectFormatAsync(input, cancellationToken),
            "StartTextractJob" => await StartTextractJobAsync(evt, input, cancellationToken),
            "ClassifyDocument" => await ClassifyDocumentAsync(input, cancellationToken),
            "SemanticExtraction" => await SemanticExtractionAsync(input, cancellationToken),
            "DetectAndRedactPII" => await DetectAndRedactPiiAsync(input, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown action: {action}")
        };
    }

    private async Task<object> DetectFormatAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (tenantId, documentoId, versionNumero, s3Key) = ExtractTriggerContext(input);

        // First step of a fresh Extraction execution — marks the document
        // as actually being worked on (vs. PENDING, set by /prepare before
        // upload even completes). Not written on a reprocess re-entry,
        // since ExtractTriggerContext already handles that shape the same
        // way either way and there's no separate signal to distinguish it
        // here — harmless to set again regardless.
        await SetEstadoAsync(documentoId, versionNumero, "EXTRACTING", cancellationToken);

        var extension = s3Key.Contains('.') ? s3Key[(s3Key.LastIndexOf('.') + 1)..].ToLowerInvariant() : "";

        string? nativeText = null;
        var needsOcr = false;

        if (extension is "txt" or "md")
        {
            var obj = await _s3.GetObjectAsync(_documentBucket, s3Key, cancellationToken);
            using var reader = new StreamReader(obj.ResponseStream, Encoding.UTF8);
            nativeText = await reader.ReadToEndAsync(cancellationToken);
        }
        else if (extension == "pdf")
        {
            var obj = await _s3.GetObjectAsync(_documentBucket, s3Key, cancellationToken);
            using var memory = new MemoryStream();
            await obj.ResponseStream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            using var pdf = PdfDocument.Open(memory);
            var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));

            if (text.Length >= PdfPigMinChars)
            {
                nativeText = text;
            }
            else
            {
                needsOcr = true;
            }
        }
        else if (extension is "png" or "jpg" or "jpeg" or "tiff")
        {
            needsOcr = true;
        }
        else
        {
            // .docx/.pptx/.xlsx native parsing (DocumentFormat.OpenXml) is a
            // real gap ADR-003 leaves open — not implemented in this pass.
            throw new NotSupportedException(
                $"No extraction route implemented yet for .{extension} (ADR-003 open question — OOXML native parsing).");
        }

        var rawTextKey = !needsOcr ? await WriteRawTextAsync(tenantId, documentoId, versionNumero, nativeText!, cancellationToken) : null;

        return new
        {
            tenantId,
            documentoId,
            versionNumero,
            s3Key,
            needsOcr,
            rawTextKey
        };
    }

    private async Task<object> StartTextractJobAsync(JsonElement evt, JsonElement input, CancellationToken cancellationToken)
    {
        var taskToken = evt.GetProperty("taskToken").GetString()!;
        var detectFormat = input.GetProperty("detectFormat").GetProperty("Payload");
        var s3Key = detectFormat.GetProperty("s3Key").GetString()!;
        var tenantId = detectFormat.GetProperty("tenantId").GetString()!;
        var documentoId = detectFormat.GetProperty("documentoId").GetString()!;
        var versionNumero = detectFormat.GetProperty("versionNumero").GetInt32();

        await _ocr.StartAsync(s3Key, tenantId, documentoId, versionNumero, taskToken, cancellationToken);

        // This Task state pauses here (.waitForTaskToken) — the return
        // value is discarded until TextractBridgeHandler calls
        // SendTaskSuccess with the real output (rawTextKey) once the async
        // Textract job completes.
        return new { };
    }

    private async Task<object> ClassifyDocumentAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var rawText = await ReadRawTextAsync(input, cancellationToken);
        var documentType = await _classification.ClassifyAsync(rawText, cancellationToken);

        return new { documentType = documentType.ToString() };
    }

    private async Task<object> SemanticExtractionAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var rawText = await ReadRawTextAsync(input, cancellationToken);
        var documentType = Enum.Parse<DocumentType>(input.GetProperty("classify").GetProperty("Payload").GetProperty("documentType").GetString()!);

        var fields = await _semanticAnalysis.AnalyzeAsync(rawText, documentType, cancellationToken);

        return new { fields };
    }

    private async Task<object> DetectAndRedactPiiAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (tenantId, documentoId, versionNumero, _) = ExtractTriggerContext(input);
        var rawText = await ReadRawTextAsync(input, cancellationToken);

        var redaction = await _piiRedaction.RedactAsync(documentoId, rawText, cancellationToken);

        var redactedKey = $"{tenantId}/documents/{documentoId}/v{versionNumero}.txt";
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _kbSourceBucket,
            Key = redactedKey,
            ContentBody = redaction.RedactedText,
            ContentType = "text/plain"
        }, cancellationToken);

        // Bedrock KB metadata sidecar convention — lets ChatService's
        // Retrieve filter by empresa_id (ADR-001's per-tenant isolation).
        var metadataJson = JsonSerializer.Serialize(new
        {
            metadataAttributes = new Dictionary<string, object>
            {
                ["empresa_id"] = new { value = new { type = "STRING", stringValue = tenantId }, includeForEmbedding = false },
                ["documento_id"] = new { value = new { type = "STRING", stringValue = documentoId }, includeForEmbedding = false }
            }
        });
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _kbSourceBucket,
            Key = $"{redactedKey}.metadata.json",
            ContentBody = metadataJson,
            ContentType = "application/json"
        }, cancellationToken);

        await SetEstadoAsync(documentoId, versionNumero, "PENDING_REVIEW", cancellationToken);

        return new { tenantId, documentoId, versionNumero };
    }

    // --- helpers ---

    private static (string TenantId, string DocumentoId, int VersionNumero, string S3Key) ExtractTriggerContext(JsonElement input)
    {
        // Two trigger shapes reach DetectFormat's input: the raw GuardDuty
        // scan-result event (initial upload), or doclens-api's
        // DocumentReprocessRequested custom event (manual reprocess) —
        // ADR-011's diagram walkthrough. Both eventually resolve to the
        // same {tenantId, documentoId, versionNumero, s3Key} tuple; for a
        // reprocess, detectFormat.Payload already carries those fields
        // directly instead of a raw S3 key needing to be parsed.
        if (input.TryGetProperty("detectFormat", out var already))
        {
            var payload = already.GetProperty("Payload");
            return (
                payload.GetProperty("tenantId").GetString()!,
                payload.GetProperty("documentoId").GetString()!,
                payload.GetProperty("versionNumero").GetInt32(),
                payload.GetProperty("s3Key").GetString()!);
        }

        var detail = input.GetProperty("detail");

        string s3Key;
        if (detail.TryGetProperty("s3ObjectDetails", out var s3Details))
        {
            s3Key = s3Details.GetProperty("objectKey").GetString()!;
        }
        else
        {
            s3Key = detail.GetProperty("s3Key").GetString()!;
        }

        // {tenantId}/documents/{documentoId}/v{n}.{ext} — DocumentUploadService's key convention.
        var parts = s3Key.Split('/');
        var tenantId = parts[0];
        var documentoId = parts[2];
        var versionNumero = int.Parse(parts[3][1..parts[3].IndexOf('.')]);

        return (tenantId, documentoId, versionNumero, s3Key);
    }

    private async Task<string> WriteRawTextAsync(
        string tenantId, string documentoId, int versionNumero, string text, CancellationToken cancellationToken)
    {
        // Intermediate, pre-redaction text — never the final KB source key.
        // Step Functions caps total state at 256KB, so this is passed by
        // reference (the S3 key), not inline in the state.
        var key = $"{tenantId}/raw-extracted/{documentoId}/v{versionNumero}.txt";
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _kbSourceBucket,
            Key = key,
            ContentBody = text,
            ContentType = "text/plain"
        }, cancellationToken);
        return key;
    }

    private async Task<string> ReadRawTextAsync(JsonElement input, CancellationToken cancellationToken)
    {
        // Either DetectFormat wrote it directly (native/PdfPig path), or
        // the Textract bridge did after resuming the OCR wait — either way
        // it's the same rawTextKey shape by the time later steps run.
        var key = input.TryGetProperty("ocr", out var ocr)
            ? ocr.GetProperty("rawTextKey").GetString()!
            : input.GetProperty("detectFormat").GetProperty("Payload").GetProperty("rawTextKey").GetString()!;

        var obj = await _s3.GetObjectAsync(_kbSourceBucket, key, cancellationToken);
        using var reader = new StreamReader(obj.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task SetEstadoAsync(string documentoId, int versionNumero, string estado, CancellationToken cancellationToken)
    {
        await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = "doclens",
            Sql = "UPDATE documento_versiones SET estado = :estado WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero",
            Parameters =
            [
                new SqlParameter { Name = "estado", Value = new Field { StringValue = estado } },
                new SqlParameter { Name = "documentoId", Value = new Field { StringValue = documentoId } },
                new SqlParameter { Name = "versionNumero", Value = new Field { LongValue = versionNumero } }
            ]
        }, cancellationToken);
    }
}
