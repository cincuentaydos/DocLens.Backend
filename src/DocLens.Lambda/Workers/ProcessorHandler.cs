using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;

namespace DocLens.Lambda.Workers;

// Processor Lambda: consumes doclens-processing-{env} (GuardDuty clean scan
// results via EventBridge, ADR-011) and moves the matching version from
// PENDING to CLEAN, which is what unlocks POST /api/documents/process.
//
// TODO(ADR-003/006/011): run OCR + semantic extraction here instead of in the
// API Lambda. Blocked on (a) persisting the document type per version (the
// Bedrock prompt needs it and it isn't stored anywhere yet) and (b) the async
// Textract flow (StartDocumentTextDetection + OcrResultHandler) for
// multi-page documents.
public class ProcessorHandler
{
    private readonly IDocumentRepository _repository;
    private readonly ILogger<ProcessorHandler> _logger;
    private readonly string? _documentBucket;

    public ProcessorHandler(IDocumentRepository repository, IConfiguration configuration, ILogger<ProcessorHandler> logger)
    {
        _repository = repository;
        _logger = logger;
        _documentBucket = configuration["DOCUMENT_BUCKET"];
    }

    public async Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var message in sqsEvent.Records ?? [])
        {
            try
            {
                if (!await HandleMessageAsync(message))
                {
                    failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
                }
            }
            catch (Exception ex)
            {
                // Transient failure (e.g. Data API throttling): let SQS
                // redeliver just this message; after maxReceiveCount it lands
                // in the DLQ instead of crash-looping the whole batch.
                _logger.LogError(ex, "Failed to process message {MessageId}", message.MessageId);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
            }
        }

        return new SQSBatchResponse(failures);
    }

    // Returns false only for messages that should be retried / dead-lettered.
    private async Task<bool> HandleMessageAsync(SQSEvent.SQSMessage message)
    {
        if (!GuardDutyScanResult.TryParse(message.Body, out var scan))
        {
            _logger.LogError("Message {MessageId} is not a GuardDuty scan result event; sending to DLQ", message.MessageId);
            return false;
        }

        if (_documentBucket is not null && scan.BucketName != _documentBucket)
        {
            _logger.LogWarning("Ignoring scan result for unexpected bucket {Bucket}", scan.BucketName);
            return true;
        }

        if (!scan.IsClean)
        {
            // The EventBridge rule only forwards NO_THREATS_FOUND; the
            // REJECTED path is an open question in ADR-004/011.
            _logger.LogWarning("Ignoring non-clean scan result {Status} for {S3Key}", scan.ScanResultStatus, scan.ObjectKey);
            return true;
        }

        if (!DocumentKey.TryParse(scan.ObjectKey, out var key))
        {
            // E.g. GuardDuty's own permission-validation object.
            _logger.LogInformation("Ignoring scan result for non-document key {S3Key}", scan.ObjectKey);
            return true;
        }

        var updated = await _repository.TransitionVersionStatusAsync(
            key.TenantId, key.DocumentId, key.Value,
            DocumentVersionStatus.Pending, DocumentVersionStatus.Clean);

        if (updated)
        {
            _logger.LogInformation(
                "Document {DocumentId} v{VersionNumber} for tenant {TenantId} marked {Status}",
                key.DocumentId, key.VersionNumber, key.TenantId, DocumentVersionStatus.Clean);
        }
        else
        {
            // Duplicate delivery, or an object with no prepared version row.
            _logger.LogWarning(
                "No PENDING version row for {S3Key} (tenant {TenantId}); nothing to update",
                key.Value, key.TenantId);
        }

        return true;
    }
}
