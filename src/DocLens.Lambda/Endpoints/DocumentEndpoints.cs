using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Services.Access;
using DocLens.Lambda.Services.Extraction;
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

        group.MapPost("/process", ProcessDocument)
            .WithName("ProcessDocument")
            .WithSummary("Extracts structured data from a clean, already-uploaded version of one of the caller's documents.");

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
        catch (InvalidDocumentIdException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (DocumentNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ProcessDocument(
        ProcessDocumentRequest request,
        IDocumentAccessGuard accessGuard,
        IDocumentExtractionService extractionService,
        CancellationToken cancellationToken)
    {
        var decision = await accessGuard.CheckProcessAccessAsync(request, cancellationToken);
        return decision switch
        {
            DocumentAccessDecision.Allowed =>
                Results.Ok(await extractionService.ExtractAsync(request, cancellationToken)),
            DocumentAccessDecision.InvalidDocumentId =>
                Results.BadRequest(new { error = "documentId must be a UUID." }),
            DocumentAccessDecision.Forbidden =>
                Results.Json(new { error = "s3Key does not belong to this document." }, statusCode: StatusCodes.Status403Forbidden),
            DocumentAccessDecision.NotFound =>
                Results.NotFound(new { error = "Document version not found." }),
            DocumentAccessDecision.NotScanned =>
                Results.Conflict(new { error = "Document version has not passed malware scanning yet." }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
