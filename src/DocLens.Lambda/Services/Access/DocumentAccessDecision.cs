namespace DocLens.Lambda.Services.Access;

public enum DocumentAccessDecision
{
    Allowed,
    InvalidDocumentId, // 400
    Forbidden,         // 403 — S3 key outside the caller's tenant/document prefix
    NotFound,          // 404 — no version row for this tenant/document/key
    NotScanned         // 409 — GuardDuty hasn't reported the object clean yet (ADR-011)
}
