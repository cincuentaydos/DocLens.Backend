using DocLens.Lambda.Models;

namespace DocLens.Lambda.Services.Classification;

public interface IDocumentClassificationService
{
    // Replaces what used to be a caller-supplied DocumentType on
    // ProcessDocumentRequest (removed with /documents/process, ADR-011) —
    // the event-driven pipeline has no caller to ask, so the Extraction
    // workflow classifies the document itself before running field
    // extraction by type.
    Task<DocumentType> ClassifyAsync(string rawText, CancellationToken cancellationToken = default);
}
