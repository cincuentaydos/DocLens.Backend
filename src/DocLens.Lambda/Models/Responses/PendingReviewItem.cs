using System.Text.Json;

namespace DocLens.Lambda.Models.Responses;

// GET /documents/reviews/pending — one open document_reviews row (the
// Review state machine is parked on its .waitForTaskToken), with the
// extracted campos the reviewer has to approve or reject.
public record PendingReviewItem
{
    public required string DocumentId { get; init; }
    public required int VersionNumber { get; init; }
    public required string Estado { get; init; }
    public JsonElement? Campos { get; init; }
    public required string ContentType { get; init; }
    public required string SubidoEn { get; init; }
    public required string EnRevisionDesde { get; init; }
}
