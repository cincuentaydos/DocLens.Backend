using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.S3;
using DocLens.Lambda.Context;
using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Services.Review;
using DocLens.Lambda.Services.Upload;

namespace DocLens.Lambda.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents")
            .WithTags("Documents");

        group.MapPost("/prepare", PrepareUpload)
            .WithName("PrepareDocumentUpload")
            .WithSummary("Generates a presigned upload URL for a new document version (ADR-005).");

        group.MapGet("/{documentId}/versions/{versionNumber:int}", GetVersion)
            .WithName("GetDocumentVersion")
            .WithSummary("Polls a document version's processing state — PENDING → ... → INDEXED (or REJECTED/NEEDS_CORRECTION/FAILED), per ADR-011.");

        // POST /documents/process is gone — ADR-011 retires the
        // client-called processing endpoint in favor of the GuardDuty →
        // EventBridge → Step Functions pipeline (infra/terraform/modules/processing).

        group.MapGet("/reviews/pending", ListPendingReviews)
            .WithName("ListPendingDocumentReviews")
            .WithSummary("Lists the caller tenant's document versions waiting on the human-review gate, with their extracted fields (ADR-011).");

        group.MapPost("/{documentId}/versions/{versionNumber:int}/review", ResolveReview)
            .WithName("ResolveDocumentReview")
            .WithSummary("Resolves the blocking human-review gate — approves (optionally with corrected fields) or rejects extracted data (ADR-011).");

        group.MapPost("/{documentId}/versions/{versionNumber:int}/reprocess", RequestReprocess)
            .WithName("RequestDocumentReprocess")
            .WithSummary("Manually re-runs Extraction for a NEEDS_CORRECTION version — never triggered automatically (ADR-011).");

        return app;
    }

    private static async Task<IResult> PrepareUpload(
        PrepareDocumentUploadRequest request,
        IDocumentUploadService uploadService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await uploadService.PrepareUploadAsync(request, cancellationToken);
            return Results.Ok(result);
        }
        catch (UnsupportedContentTypeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (DocumentNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetVersion(
        string documentId,
        int versionNumber,
        ITenantContext tenantContext,
        IAmazonRDSDataService rdsData,
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var response = await rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = configuration["AURORA_CLUSTER"]!,
            SecretArn = configuration["AURORA_SECRET"]!,
            Database = "doclens",
            Sql = """
                SELECT v.estado, v.campos, v.s3_key, v.content_type, v.creado_en, v.procesado_en, v.tipo_documento,
                       (SELECT json_agg(json_build_object('marcador', p.placeholder, 'tipo', p.tipo_entidad, 'valor', p.valor_real) ORDER BY p.creado_en)
                 FROM pii_mappings p WHERE p.documento_id = v.documento_id)::text
                FROM documento_versiones v
                JOIN documentos d ON d.id = v.documento_id
                WHERE v.documento_id = :documentId::uuid AND v.version_numero = :versionNumber
                  AND d.empresa_id = :empresaId
                """,
            Parameters =
            [
                new SqlParameter { Name = "documentId", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "versionNumber", Value = new Field { LongValue = versionNumber } },
                new SqlParameter { Name = "empresaId", Value = new Field { StringValue = tenantContext.TenantId } }
            ]
        }, cancellationToken);

        if (response.Records.Count == 0)
        {
            return Results.NotFound(new { error = $"Document {documentId} version {versionNumber} not found." });
        }

        var record = response.Records[0];
        var textoRedactado = await TryReadRedactedTextAsync(
            s3, configuration, logger, tenantContext.TenantId, documentId, versionNumber, cancellationToken);

        return Results.Ok(new
        {
            documentId,
            versionNumber,
            estado = record[0].StringValue,
            // Data API omits isNull on a non-null value, so test the value
            // itself — `IsNull ?? true` read every campos as null.
            campos = record[1].StringValue is null
                ? (object?)null
                : System.Text.Json.JsonDocument.Parse(record[1].StringValue!).RootElement,
            s3Key = record[2].StringValue,
            contentType = record[3].StringValue,
            creadoEn = record[4].StringValue,
            procesadoEn = record[5].StringValue,
            tipoDocumento = record[6].StringValue,
            // What DetectAndRedactPII replaced before the text reached the
            // Knowledge Base — lets the reviewer check the redaction, not
            // just the extracted campos (which come from the original text).
            pii = record[7].StringValue is null
                ? (object?)null
                : System.Text.Json.JsonDocument.Parse(record[7].StringValue!).RootElement,
            // The exact text Bedrock KB ingests on approval — null until
            // DetectAndRedactPII has written it (or if KB_SOURCE_BUCKET
            // isn't wired into this Lambda yet).
            textoRedactado
        });
    }

    // Same key DetectAndRedactPII writes: {tenantId}/documents/{documentoId}/v{n}.txt
    // in kb_source. Never throws — the redacted text is a review aid, so its
    // absence shouldn't fail the whole version lookup.
    private static async Task<string?> TryReadRedactedTextAsync(
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger logger,
        string tenantId,
        string documentId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var bucket = configuration["KB_SOURCE_BUCKET"];
        if (string.IsNullOrEmpty(bucket))
        {
            return null;
        }

        var key = $"{tenantId}/documents/{documentId}/v{versionNumber}.txt";
        try
        {
            using var obj = await s3.GetObjectAsync(bucket, key, cancellationToken);
            using var reader = new StreamReader(obj.ResponseStream, System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // NotFound: not redacted yet. Forbidden: S3 answers a missing key
            // with 403 when the caller lacks s3:ListBucket, so both mean the
            // same thing here.
            logger.LogInformation(
                "No redacted text for document {DocumentId} v{VersionNumber}, tenant {TenantId} ({StatusCode})",
                documentId, versionNumber, tenantId, ex.StatusCode);
            return null;
        }
    }

    private static async Task<IResult> ListPendingReviews(
        ITenantContext tenantContext,
        IReviewService reviewService,
        CancellationToken cancellationToken)
    {
        var pending = await reviewService.ListPendingAsync(tenantContext.TenantId, cancellationToken);
        return Results.Ok(pending);
    }

    private static async Task<IResult> ResolveReview(
        string documentId,
        int versionNumber,
        ReviewDecisionRequest request,
        ITenantContext tenantContext,
        IReviewService reviewService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Revisor identity comes straight off the JWT — TenantContext only
        // carries TenantId today, extending it for one field used in one
        // endpoint isn't worth the wider surface change.
        var revisorId = httpContext.User.FindFirst("sub")?.Value;

        var resolved = await reviewService.ResolveAsync(
            tenantContext.TenantId, documentId, versionNumber, request.Approved, request.CorrectedFields, request.Comentario, revisorId, cancellationToken);

        return resolved
            ? Results.Ok(new { resolved = true })
            : Results.NotFound(new { error = $"No pending review for document {documentId} version {versionNumber}." });
    }

    private static async Task<IResult> RequestReprocess(
        string documentId,
        int versionNumber,
        ITenantContext tenantContext,
        IAmazonRDSDataService rdsData,
        IAmazonEventBridge eventBridge,
        IConfiguration configuration,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var auroraClusterArn = configuration["AURORA_CLUSTER"]!;
        var auroraSecretArn = configuration["AURORA_SECRET"]!;

        var lookup = await rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = auroraClusterArn,
            SecretArn = auroraSecretArn,
            Database = "doclens",
            Sql = """
                SELECT v.s3_key, v.estado
                FROM documento_versiones v
                JOIN documentos d ON d.id = v.documento_id
                WHERE v.documento_id = :documentId::uuid AND v.version_numero = :versionNumber
                  AND d.empresa_id = :empresaId
                """,
            Parameters =
            [
                new SqlParameter { Name = "documentId", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "versionNumber", Value = new Field { LongValue = versionNumber } },
                new SqlParameter { Name = "empresaId", Value = new Field { StringValue = tenantContext.TenantId } }
            ]
        }, cancellationToken);

        if (lookup.Records.Count == 0)
        {
            return Results.NotFound(new { error = $"Document {documentId} version {versionNumber} not found." });
        }

        var (s3Key, estado) = (lookup.Records[0][0].StringValue!, lookup.Records[0][1].StringValue!);
        if (estado != "NEEDS_CORRECTION")
        {
            return Results.BadRequest(new { error = $"Document is in state {estado}, not NEEDS_CORRECTION — nothing to reprocess." });
        }

        var detail = System.Text.Json.JsonSerializer.Serialize(new
        {
            tenantId = tenantContext.TenantId,
            documentoId = documentId,
            versionNumero = versionNumber,
            s3Key
        });

        await eventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = "doclens.processing",
                    DetailType = "DocumentReprocessRequested",
                    Detail = detail
                }
            ]
        }, cancellationToken);

        logger.LogInformation("Reprocess requested for document {DocumentId} version {VersionNumber}", documentId, versionNumber);

        return Results.Accepted(value: new { requested = true });
    }
}
