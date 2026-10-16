using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.S3;
using Amazon.S3.Model;
using DocLens.Lambda.Context;
using DocLens.Lambda.Models;
using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Models.Responses;
using AWS.Lambda.Powertools.Tracing;

namespace DocLens.Lambda.Services.Upload;

public class DocumentUploadService : IDocumentUploadService
{
    // Matches modules/data/main.tf's hardcoded database_name — not yet
    // exposed as a Lambda environment variable (see AURORA_CLUSTER/AURORA_SECRET).
    private const string Database = "doclens";
    private static readonly TimeSpan UploadUrlTtl = TimeSpan.FromMinutes(15); // ADR-005

    private readonly IAmazonS3 _s3;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<DocumentUploadService> _logger;
    private readonly string _documentBucket;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public DocumentUploadService(
        IAmazonS3 s3,
        IAmazonRDSDataService rdsData,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<DocumentUploadService> logger)
    {
        _s3 = s3;
        _rdsData = rdsData;
        _tenantContext = tenantContext;
        _logger = logger;
        _documentBucket = configuration["DOCUMENT_BUCKET"]
            ?? throw new InvalidOperationException("DOCUMENT_BUCKET is not set.");
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task<PrepareDocumentUploadResult> PrepareUploadAsync(
        PrepareDocumentUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedDocumentFormats.TryGetExtension(request.ContentType, out var extension))
        {
            throw new UnsupportedContentTypeException(request.ContentType);
        }

        var tenantId = _tenantContext.TenantId;
        string documentId;
        int versionNumber;

        if (request.DocumentId is not null)
        {
            var latestVersion = await GetLatestVersionAsync(request.DocumentId, tenantId, cancellationToken);
            documentId = request.DocumentId;
            versionNumber = latestVersion + 1;
            await UpdateLatestVersionAsync(documentId, versionNumber, cancellationToken);
        }
        else
        {
            documentId = await InsertDocumentAsync(tenantId, cancellationToken);
            versionNumber = 1;
        }

        var s3Key = $"{tenantId}/documents/{documentId}/v{versionNumber}.{extension}";
        var expiresAt = DateTimeOffset.UtcNow.Add(UploadUrlTtl);

        // Signing a presigned URL is a local cryptographic operation — no
        // AWS API call happens here, so no extra IAM permission is needed
        // beyond what the eventual PUT itself requires (already granted:
        // s3:PutObject + kms:GenerateDataKey, see modules/processing/iam.tf).
        var uploadUrl = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _documentBucket,
            Key = s3Key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = request.ContentType
        });

        await InsertVersionAsync(documentId, versionNumber, s3Key, request.ContentType, cancellationToken);

        _logger.LogInformation(
            "Prepared upload for document {DocumentId} v{VersionNumber}, tenant {TenantId}",
            documentId, versionNumber, tenantId);

        return new PrepareDocumentUploadResult
        {
            DocumentId = documentId,
            VersionNumber = versionNumber,
            UploadUrl = uploadUrl,
            ExpiresAt = expiresAt
        };
    }

    private async Task<int> GetLatestVersionAsync(string documentId, string tenantId, CancellationToken cancellationToken)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "SELECT latest_version FROM documentos WHERE id = :id::uuid AND empresa_id = :empresaId",
            Parameters =
            [
                new SqlParameter { Name = "id", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "empresaId", Value = new Field { StringValue = tenantId } }
            ]
        }, cancellationToken);

        if (response.Records.Count == 0)
        {
            throw new DocumentNotFoundException(documentId);
        }

        return (int)response.Records[0][0].LongValue!.Value;
    }

    private async Task UpdateLatestVersionAsync(string documentId, int versionNumber, CancellationToken cancellationToken)
    {
        await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "UPDATE documentos SET latest_version = :versionNumber, actualizado_en = now() WHERE id = :id::uuid",
            Parameters =
            [
                new SqlParameter { Name = "versionNumber", Value = new Field { LongValue = versionNumber } },
                new SqlParameter { Name = "id", Value = new Field { StringValue = documentId } }
            ]
        }, cancellationToken);
    }

    private async Task<string> InsertDocumentAsync(string tenantId, CancellationToken cancellationToken)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "INSERT INTO documentos (empresa_id, latest_version) VALUES (:empresaId, 1) RETURNING id",
            Parameters =
            [
                new SqlParameter { Name = "empresaId", Value = new Field { StringValue = tenantId } }
            ]
        }, cancellationToken);

        return response.Records[0][0].StringValue!;
    }

    private async Task InsertVersionAsync(
        string documentId, int versionNumber, string s3Key, string contentType, CancellationToken cancellationToken)
    {
        await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = """
                INSERT INTO documento_versiones (documento_id, version_numero, s3_key, content_type, estado)
                VALUES (:documentoId::uuid, :versionNumero, :s3Key, :contentType, 'PENDING')
                """,
            Parameters =
            [
                new SqlParameter { Name = "documentoId", Value = new Field { StringValue = documentId } },
                new SqlParameter { Name = "versionNumero", Value = new Field { LongValue = versionNumber } },
                new SqlParameter { Name = "s3Key", Value = new Field { StringValue = s3Key } },
                new SqlParameter { Name = "contentType", Value = new Field { StringValue = contentType } }
            ]
        }, cancellationToken);
    }
}
