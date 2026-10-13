using DocLens.Lambda.Models.Responses;

namespace DocLens.Lambda.Services.Review;

public interface IReviewService
{
    // Called by review_worker's EnterReview step — persists the SFN task
    // token to document_reviews so the API-side resolve below can find it
    // later (documento_versiones.estado is already PENDING_REVIEW, written
    // by the Extraction workflow's DetectAndRedactPII step).
    Task EnterReviewAsync(
        string documentoId, int versionNumero, string taskToken, CancellationToken cancellationToken = default);

    // Called by review_worker's WriteApproved step, after the SFN resumes.
    Task WriteApprovedAsync(
        string documentoId, int versionNumero, CancellationToken cancellationToken = default);

    // Called by review_worker's WriteNeedsCorrection step.
    Task WriteNeedsCorrectionAsync(
        string documentoId, int versionNumero, CancellationToken cancellationToken = default);

    // Called from GET /documents/reviews/pending (doclens-api) — every open
    // document_reviews row for the caller's tenant, oldest first. Lists
    // what ResolveAsync can actually resolve: a version can already be
    // PENDING_REVIEW before EnterReview has persisted its token.
    Task<IReadOnlyList<PendingReviewItem>> ListPendingAsync(
        string tenantId, CancellationToken cancellationToken = default);

    // Called from POST /documents/{id}/versions/{n}/review (doclens-api) —
    // looks up the pending document_reviews row, records who decided what,
    // and resolves the EnterReview .waitForTaskToken pause via
    // SendTaskSuccess/Failure. Returns false if there's no pending review
    // for this document version in the caller's tenant (already resolved,
    // never entered, or another tenant's document).
    Task<bool> ResolveAsync(
        string tenantId,
        string documentoId,
        int versionNumero,
        bool approved,
        IReadOnlyDictionary<string, string>? correctedFields,
        string? comentario,
        string? revisorId,
        CancellationToken cancellationToken = default);
}
