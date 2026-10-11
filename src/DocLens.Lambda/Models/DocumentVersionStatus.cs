namespace DocLens.Lambda.Models;

// Values stored in documento_versiones.estado (see DocLens.Infra/migrations).
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
