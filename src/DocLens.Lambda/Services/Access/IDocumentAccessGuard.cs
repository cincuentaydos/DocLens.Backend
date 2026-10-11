using DocLens.Lambda.Models.Requests;

namespace DocLens.Lambda.Services.Access;

public interface IDocumentAccessGuard
{
    Task<DocumentAccessDecision> CheckProcessAccessAsync(
        ProcessDocumentRequest request,
        CancellationToken cancellationToken = default);
}
