using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using DocLens.Lambda.Services.Ocr;

namespace DocLens.Lambda.Handlers;

// Triggered by the textract_completion SNS topic (ADR-003/011) — resolves
// the Extraction state machine's StartTextractJob .waitForTaskToken pause.
// Parsed manually (not via Amazon.Lambda.SNSEvents) to keep every non-HTTP
// handler in this project on the same plain JsonElement contract — see
// Program.cs's dispatch.
public class TextractBridgeHandler
{
    private readonly IOcrService _ocr;
    private readonly IAmazonS3 _s3;
    private readonly IAmazonStepFunctions _stepFunctions;
    private readonly ILogger<TextractBridgeHandler> _logger;
    private readonly string _kbSourceBucket;

    public TextractBridgeHandler(
        IOcrService ocr,
        IAmazonS3 s3,
        IAmazonStepFunctions stepFunctions,
        IConfiguration configuration,
        ILogger<TextractBridgeHandler> logger)
    {
        _ocr = ocr;
        _s3 = s3;
        _stepFunctions = stepFunctions;
        _logger = logger;
        _kbSourceBucket = configuration["KB_SOURCE_BUCKET"]
            ?? throw new InvalidOperationException("KB_SOURCE_BUCKET is not set.");
    }

    public async Task<object> HandleAsync(JsonElement snsEvent, CancellationToken cancellationToken = default)
    {
        foreach (var record in snsEvent.GetProperty("Records").EnumerateArray())
        {
            var messageJson = record.GetProperty("Sns").GetProperty("Message").GetString()!;
            using var message = JsonDocument.Parse(messageJson);
            var root = message.RootElement;

            var jobId = root.GetProperty("JobId").GetString()!;
            var jobTag = root.GetProperty("JobTag").GetString()!;
            var status = root.GetProperty("Status").GetString()!;

            var correlation = await _ocr.ResolveJobTagAsync(jobTag, cancellationToken);

            if (status != "SUCCEEDED")
            {
                _logger.LogWarning("Textract job {JobId} finished with status {Status}", jobId, status);
                await _stepFunctions.SendTaskFailureAsync(new SendTaskFailureRequest
                {
                    TaskToken = correlation.TaskToken,
                    Error = "TextractJobFailed",
                    Cause = $"Textract job {jobId} finished with status {status}."
                }, cancellationToken);
                continue;
            }

            var text = await _ocr.GetResultAsync(jobId, cancellationToken);

            var rawTextKey = $"{correlation.TenantId}/raw-extracted/{correlation.DocumentoId}/v{correlation.VersionNumero}.txt";
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _kbSourceBucket,
                Key = rawTextKey,
                ContentBody = text,
                ContentType = "text/plain"
            }, cancellationToken);

            await _stepFunctions.SendTaskSuccessAsync(new SendTaskSuccessRequest
            {
                TaskToken = correlation.TaskToken,
                Output = JsonSerializer.Serialize(new { rawTextKey })
            }, cancellationToken);

            _logger.LogInformation("Resumed extraction for documento {DocumentoId} v{VersionNumero} after Textract job {JobId}",
                correlation.DocumentoId, correlation.VersionNumero, jobId);
        }

        return new { };
    }
}
