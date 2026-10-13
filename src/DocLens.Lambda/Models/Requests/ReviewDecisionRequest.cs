namespace DocLens.Lambda.Models.Requests;

// POST /documents/{documentId}/versions/{versionNumber}/review — resolves
// the doclens-document-review-{env} state machine's EnterReview
// .waitForTaskToken pause (ADR-011). CorrectedFields lets the reviewer fix
// values before approving; ignored when Approved is false.
public record ReviewDecisionRequest(
    bool Approved,
    Dictionary<string, string>? CorrectedFields,
    string? Comentario
);
