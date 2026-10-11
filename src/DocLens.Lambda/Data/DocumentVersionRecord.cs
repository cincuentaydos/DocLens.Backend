namespace DocLens.Lambda.Data;

public sealed record DocumentVersionRecord(Guid DocumentId, int VersionNumber, string S3Key);
