using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;

namespace DocLens.Lambda.Workers;

// OCR result Lambda: consumes doclens-ocr-result-{env} (Textract async job
// completions via SNS, ADR-003).
//
// TODO(ADR-003): fetch the result pages with GetDocumentTextDetection and
// continue the extraction pipeline. Nothing starts async Textract jobs yet
// (TextractOcrService is still synchronous), so for now this only validates
// and logs completions so the queue drains instead of crash-looping.
public class OcrResultHandler
{
    private readonly ILogger<OcrResultHandler> _logger;

    public OcrResultHandler(ILogger<OcrResultHandler> logger) => _logger = logger;

    public Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var message in sqsEvent.Records ?? [])
        {
            if (!TextractCompletion.TryParse(message.Body, out var completion))
            {
                _logger.LogError("Message {MessageId} is not a Textract completion notification; sending to DLQ", message.MessageId);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
                continue;
            }

            if (completion.Succeeded)
            {
                _logger.LogInformation(
                    "Textract job {JobId} ({Api}) succeeded for {S3Key}; result retrieval not implemented yet",
                    completion.JobId, completion.Api, completion.S3ObjectName);
            }
            else
            {
                _logger.LogWarning(
                    "Textract job {JobId} ({Api}) finished with status {Status} for {S3Key}",
                    completion.JobId, completion.Api, completion.Status, completion.S3ObjectName);
            }
        }

        return Task.FromResult(new SQSBatchResponse(failures));
    }
}
