namespace DocLens.Lambda.Data;

public interface IDocumentRepository
{
    // Creates a new document (version 1) and its first version row in a single transaction.
    Task<DocumentVersionRecord> CreateDocumentAsync(
        string tenantId,
        string contentType,
        Func<Guid, int, string> s3KeyFactory,
        CancellationToken cancellationToken = default);

    // Atomically increments documentos.latest_version for a document owned by
    // tenantId and inserts the matching version row, in a single transaction.
    // Returns null when the document doesn't exist for that tenant.
    Task<DocumentVersionRecord?> AddVersionAsync(
        string tenantId,
        Guid documentId,
        string contentType,
        Func<Guid, int, string> s3KeyFactory,
        CancellationToken cancellationToken = default);

    // Status (documento_versiones.estado) of the version stored at s3Key, or
    // null when no such version exists for that tenant and document.
    Task<string?> GetVersionStatusAsync(
        string tenantId,
        Guid documentId,
        string s3Key,
        CancellationToken cancellationToken = default);

    // Moves a version from one status to another. Returns false when no row
    // matched (unknown key, other tenant, or already in a different status).
    Task<bool> TransitionVersionStatusAsync(
        string tenantId,
        Guid documentId,
        string s3Key,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default);
}
