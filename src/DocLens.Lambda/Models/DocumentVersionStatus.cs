namespace DocLens.Lambda.Models;

// System status values stored in document_versions.status (CHECK constraint
// document_versions_status_check in DocLens.Infra/migrations/002_documents.sql).
// They are system state, not business content, so they stay English.
public static class DocumentVersionStatus
{
    // Presigned URL issued; object not uploaded or not yet scanned by GuardDuty.
    public const string Pending = "PENDING";

    // GuardDuty Malware Protection reported NO_THREATS_FOUND (ADR-004/011).
    public const string Clean = "CLEAN";

    // Structured extraction completed.
    public const string Processed = "PROCESSED";

    // GuardDuty reported THREATS_FOUND / UNSCANNABLE. Not wired yet (ADR-011 open question).
    public const string Rejected = "REJECTED";
}
