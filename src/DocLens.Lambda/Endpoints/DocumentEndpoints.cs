using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
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
        IAmazonRDSDataService rdsData,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var response = await rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = configuration["AURORA_CLUSTER"]!,
            SecretArn = configuration["AURORA_SECRET"]!,
            Database = "doclens",
            Sql = """
                SELECT estado, campos, s3_key, content_type, creado_en, procesado_en
                FROM documento_versiones
                WHERE documento_id = :documentId::uuid AND version_numero = :versionNumber
                """,
            Parameters =
            [
                new SqlParameter { Name = "documentId", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "versionNumber", Value = new Field { LongValue = versionNumber } }
            ]
        }, cancellationToken);

        if (response.Records.Count == 0)
        {
            return Results.NotFound(new { error = $"Document {documentId} version {versionNumber} not found." });
        }

        var record = response.Records[0];
        return Results.Ok(new
        {
            documentId,
            versionNumber,
            estado = record[0].StringValue,
            campos = record[1].IsNull ?? true
                ? (object?)null
                : System.Text.Json.JsonDocument.Parse(record[1].StringValue!).RootElement,
            s3Key = record[2].StringValue,
            contentType = record[3].StringValue,
            creadoEn = record[4].StringValue,
            procesadoEn = record[5].StringValue
        });
    }

    private static async Task<IResult> ResolveReview(
        string documentId,
        int versionNumber,
        ReviewDecisionRequest request,
        IReviewService reviewService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Revisor identity comes straight off the JWT — TenantContext only
        // carries TenantId today, extending it for one field used in one
        // endpoint isn't worth the wider surface change.
        var revisorId = httpContext.User.FindFirst("sub")?.Value;

        var resolved = await reviewService.ResolveAsync(
            documentId, versionNumber, request.Approved, request.CorrectedFields, request.Comentario, revisorId, cancellationToken);

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
            Sql = "SELECT s3_key, estado FROM documento_versiones WHERE documento_id = :documentId::uuid AND version_numero = :versionNumber",
            Parameters =
            [
                new SqlParameter { Name = "documentId", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "versionNumber", Value = new Field { LongValue = versionNumber } }
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
