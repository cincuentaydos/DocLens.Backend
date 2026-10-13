namespace DocLens.Lambda.Services.Ocr;

public record TextractJobCorrelation(string TaskToken, string TenantId, string DocumentoId, int VersionNumero);

public interface IOcrService
{
    Task<string> ExtractTextAsync(string s3Key, CancellationToken cancellationToken = default);

    // Starts an async Textract job (ADR-003/011). Returns the short job tag
    // Textract will echo back on its SNS completion notification — the
    // task token itself can't be the JobTag directly (Textract's 64-char
    // limit; SFN task tokens run much longer), so the mapping is persisted
    // in textract_jobs and looked up via ResolveJobTagAsync.
    Task<string> StartAsync(
        string s3Key,
        string tenantId,
        string documentoId,
        int versionNumero,
        string taskToken,
        CancellationToken cancellationToken = default);

    // Called by the Textract completion bridge — the SNS message's JobTag
    // field resolves back to the SFN task token that's waiting on it.
    Task<TextractJobCorrelation> ResolveJobTagAsync(string jobTag, CancellationToken cancellationToken = default);

    // Fetches the completed job's extracted text via the real Textract
    // JobId (distinct from the job tag above).
    Task<string> GetResultAsync(string textractJobId, CancellationToken cancellationToken = default);
}
