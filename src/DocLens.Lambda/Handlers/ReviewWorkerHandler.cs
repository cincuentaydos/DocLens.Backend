using System.Text.Json;
using DocLens.Lambda.Services.Review;
using AWS.Lambda.Powertools.Tracing;

namespace DocLens.Lambda.Handlers;

// Behind doclens-document-review-{env} (ADR-011). Triggered fresh by the
// DocumentExtracted EventBridge event, so the execution's root input is
// the EventBridge envelope — the actual payload lives under "detail"
// (EmitDocumentExtracted's ASL never configured an InputTransformer, so
// EventBridge passes the whole event through).
public class ReviewWorkerHandler
{
    private readonly IReviewService _review;
    private readonly ILogger<ReviewWorkerHandler> _logger;

    public ReviewWorkerHandler(IReviewService review, ILogger<ReviewWorkerHandler> logger)
    {
        _review = review;
        _logger = logger;
    }

    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task<object> HandleAsync(JsonElement evt, CancellationToken cancellationToken = default)
    {
        var action = evt.GetProperty("action").GetString();
        var input = evt.GetProperty("input");
        var detail = input.GetProperty("detail");
        var documentoId = detail.GetProperty("documentoId").GetString()!;
        var versionNumero = detail.GetProperty("versionNumero").GetInt32();

        _logger.LogInformation("ReviewWorker handling action {Action} for documento {DocumentoId} v{VersionNumero}",
            action, documentoId, versionNumero);

        switch (action)
        {
            case "EnterReview":
                var taskToken = evt.GetProperty("taskToken").GetString()!;
                await _review.EnterReviewAsync(documentoId, versionNumero, taskToken, cancellationToken);
                return new { };

            case "WriteApproved":
                await _review.WriteApprovedAsync(documentoId, versionNumero, cancellationToken);
                // Carries {tenantId, documentoId, versionNumero} forward as
                // the EmitDocumentApproved event's Detail — the Sync
                // workflow needs the same tuple DetectFormat parsed.
                return new
                {
                    tenantId = detail.GetProperty("tenantId").GetString(),
                    documentoId,
                    versionNumero
                };

            case "WriteNeedsCorrection":
                await _review.WriteNeedsCorrectionAsync(documentoId, versionNumero, cancellationToken);
                return new { };

            default:
                throw new InvalidOperationException($"Unknown action: {action}");
        }
    }
}
