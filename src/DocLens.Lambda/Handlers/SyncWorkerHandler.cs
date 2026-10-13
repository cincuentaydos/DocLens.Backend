using System.Text.Json;
using DocLens.Lambda.Services.Sync;

namespace DocLens.Lambda.Handlers;

// Behind doclens-document-kb-sync-{env} (ADR-011). Triggered fresh by the
// DocumentApproved EventBridge event — same "payload lives under detail"
// shape as ReviewWorkerHandler.
public class SyncWorkerHandler
{
    private readonly IKbSyncService _kbSync;
    private readonly ILogger<SyncWorkerHandler> _logger;

    public SyncWorkerHandler(IKbSyncService kbSync, ILogger<SyncWorkerHandler> logger)
    {
        _kbSync = kbSync;
        _logger = logger;
    }

    public async Task<object> HandleAsync(JsonElement evt, CancellationToken cancellationToken = default)
    {
        var action = evt.GetProperty("action").GetString();
        var input = evt.GetProperty("input");
        var detail = input.GetProperty("detail");
        var tenantId = detail.GetProperty("tenantId").GetString()!;
        var documentoId = detail.GetProperty("documentoId").GetString()!;
        var versionNumero = detail.GetProperty("versionNumero").GetInt32();

        _logger.LogInformation("SyncWorker handling action {Action} for documento {DocumentoId} v{VersionNumero}",
            action, documentoId, versionNumero);

        switch (action)
        {
            case "PrepareContent":
                var exists = await _kbSync.ContentExistsAsync(tenantId, documentoId, versionNumero, cancellationToken);
                if (!exists)
                {
                    throw new InvalidOperationException(
                        $"Redacted content missing in kb_source for documento {documentoId} v{versionNumero} — Extraction should have written it.");
                }
                return new { };

            case "StartIngestionJob":
                var ingestionJobId = await _kbSync.StartIngestionJobAsync(cancellationToken);
                return new { ingestionJobId };

            case "GetIngestionJob":
                var jobId = input.GetProperty("ingestion").GetProperty("Payload").GetProperty("ingestionJobId").GetString()!;
                var status = await _kbSync.GetIngestionJobStatusAsync(jobId, cancellationToken);
                return new { status };

            case "WriteIndexed":
                await _kbSync.WriteIndexedAsync(documentoId, versionNumero, cancellationToken);
                return new { };

            case "WriteFailed":
                await _kbSync.WriteFailedAsync(documentoId, versionNumero, cancellationToken);
                return new { };

            default:
                throw new InvalidOperationException($"Unknown action: {action}");
        }
    }
}
