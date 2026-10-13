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

    // Called from POST /documents/{id}/versions/{n}/review (doclens-api) —
    // looks up the pending document_reviews row, records who decided what,
    // and resolves the EnterReview .waitForTaskToken pause via
    // SendTaskSuccess/Failure. Returns false if there's no pending review
    // for this document version (already resolved, or never entered).
    Task<bool> ResolveAsync(
        string documentoId,
        int versionNumero,
        bool approved,
        IReadOnlyDictionary<string, string>? correctedFields,
        string? comentario,
        string? revisorId,
        CancellationToken cancellationToken = default);
}
