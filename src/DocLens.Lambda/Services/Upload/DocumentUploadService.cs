using Amazon.S3;
using Amazon.S3.Model;
using DocLens.Lambda.Context;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;
using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Models.Responses;

namespace DocLens.Lambda.Services.Upload;

public class DocumentUploadService : IDocumentUploadService
{
    private static readonly TimeSpan UploadUrlTtl = TimeSpan.FromMinutes(15); // ADR-005

    private readonly IAmazonS3 _s3;
    private readonly IDocumentRepository _repository;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<DocumentUploadService> _logger;
    private readonly string _documentBucket;

    public DocumentUploadService(
        IAmazonS3 s3,
        IDocumentRepository repository,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<DocumentUploadService> logger)
    {
        _s3 = s3;
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
        _documentBucket = configuration["DOCUMENT_BUCKET"]
            ?? throw new InvalidOperationException("DOCUMENT_BUCKET is not set.");
    }

    public async Task<PrepareDocumentUploadResult> PrepareUploadAsync(
        PrepareDocumentUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedDocumentFormats.TryGetExtension(request.ContentType, out var extension))
        {
            throw new UnsupportedContentTypeException(request.ContentType);
        }

        var tenantId = _tenantContext.TenantId;
        string BuildKey(Guid documentId, int version) => DocumentKey.Build(tenantId, documentId, version, extension);

        DocumentVersionRecord version;
        if (request.DocumentId is not null)
        {
            if (!Guid.TryParse(request.DocumentId, out var documentId))
            {
                throw new InvalidDocumentIdException(request.DocumentId);
            }

            version = await _repository.AddVersionAsync(tenantId, documentId, request.ContentType, BuildKey, cancellationToken)
                ?? throw new DocumentNotFoundException(request.DocumentId);
        }
        else
        {
            version = await _repository.CreateDocumentAsync(tenantId, request.ContentType, BuildKey, cancellationToken);
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(UploadUrlTtl);

        // Signing a presigned URL is a local cryptographic operation — no
        // AWS API call happens here. It runs only after the version row is
        // committed, so every URL handed out has a matching PENDING row.
        var uploadUrl = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _documentBucket,
            Key = version.S3Key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = request.ContentType
        });

        _logger.LogInformation(
            "Prepared upload for document {DocumentId} v{VersionNumber}, tenant {TenantId}",
            version.DocumentId, version.VersionNumber, tenantId);

        return new PrepareDocumentUploadResult
        {
            DocumentId = version.DocumentId.ToString(),
            VersionNumber = version.VersionNumber,
            UploadUrl = uploadUrl,
            ExpiresAt = expiresAt
        };
    }
}
