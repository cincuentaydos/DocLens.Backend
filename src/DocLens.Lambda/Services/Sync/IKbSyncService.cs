namespace DocLens.Lambda.Services.Sync;

public interface IKbSyncService
{
    // Confirms the redacted text Extraction wrote to doclens-kb-source-{env}
    // exists at the expected key for this documento/version — no scanning,
    // the key is derived deterministically (ADR-011).
    Task<bool> ContentExistsAsync(
        string tenantId, string documentoId, int versionNumero, CancellationToken cancellationToken = default);

    Task<string> StartIngestionJobAsync(CancellationToken cancellationToken = default);

    // Returns one of Bedrock's IngestionJobStatus values (STARTING,
    // IN_PROGRESS, COMPLETE, FAILED) — the Sync workflow polls this since
    // StartIngestionJob has no native completion event (verified 2026-09-28).
    Task<string> GetIngestionJobStatusAsync(string ingestionJobId, CancellationToken cancellationToken = default);

    Task WriteIndexedAsync(string documentoId, int versionNumero, CancellationToken cancellationToken = default);

    Task WriteFailedAsync(string documentoId, int versionNumero, CancellationToken cancellationToken = default);
}
