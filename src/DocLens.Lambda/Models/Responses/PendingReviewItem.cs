using System.Text.Json;

namespace DocLens.Lambda.Models.Responses;

// GET /documents/reviews/pending — one open document_reviews row (the
// Review state machine is parked on its .waitForTaskToken), with the
// extracted campos the reviewer has to approve or reject, plus the PII
// that was redacted before the text reached the Knowledge Base (Pii: an
// array of { marcador, tipo, valor }, or null if nothing was detected).
public record PendingReviewItem
{
    public required string DocumentId { get; init; }
    public required int VersionNumber { get; init; }
    public required string Estado { get; init; }
    public JsonElement? Campos { get; init; }
    public required string ContentType { get; init; }
    public required string SubidoEn { get; init; }
    public required string EnRevisionDesde { get; init; }
    public string? TipoDocumento { get; init; }
    public JsonElement? Pii { get; init; }
}
