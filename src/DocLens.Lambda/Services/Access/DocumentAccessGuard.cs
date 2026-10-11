using DocLens.Lambda.Context;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;
using DocLens.Lambda.Models.Requests;

namespace DocLens.Lambda.Services.Access;

// Decides whether the current tenant may run extraction over a given S3 key.
// Without this, any authenticated user could pass an arbitrary key and have
// another tenant's (or a not-yet-scanned) object sent through Textract and
// Bedrock.
public class DocumentAccessGuard : IDocumentAccessGuard
{
    private static readonly HashSet<string> ProcessableStatuses =
    [
        DocumentVersionStatus.Clean,
        DocumentVersionStatus.Processed
    ];

    private readonly IDocumentRepository _repository;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<DocumentAccessGuard> _logger;

    public DocumentAccessGuard(
        IDocumentRepository repository,
        ITenantContext tenantContext,
        ILogger<DocumentAccessGuard> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<DocumentAccessDecision> CheckProcessAccessAsync(
        ProcessDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.DocumentId, out var documentId))
        {
            return DocumentAccessDecision.InvalidDocumentId;
        }

        var tenantId = _tenantContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId)
            || !DocumentKey.TryParse(request.S3Key, out var key)
            || key.TenantId != tenantId
            || key.DocumentId != documentId)
        {
            _logger.LogWarning(
                "Rejected process request for document {DocumentId}: key {S3Key} is outside tenant {TenantId}'s prefix",
                request.DocumentId, request.S3Key, tenantId);
            return DocumentAccessDecision.Forbidden;
        }

        var status = await _repository.GetVersionStatusAsync(tenantId, documentId, key.Value, cancellationToken);
        if (status is null)
        {
            return DocumentAccessDecision.NotFound;
        }

        return ProcessableStatuses.Contains(status)
            ? DocumentAccessDecision.Allowed
            : DocumentAccessDecision.NotScanned;
    }
}
