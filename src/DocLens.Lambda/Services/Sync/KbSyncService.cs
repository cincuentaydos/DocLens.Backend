using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.S3;

namespace DocLens.Lambda.Services.Sync;

public class KbSyncService : IKbSyncService
{
    private const string Database = "doclens";

    private readonly IAmazonS3 _s3;
    private readonly IAmazonBedrockAgent _bedrockAgent;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly ILogger<KbSyncService> _logger;
    private readonly string _kbSourceBucket;
    private readonly string _knowledgeBaseId;
    private readonly string _dataSourceId;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public KbSyncService(
        IAmazonS3 s3,
        IAmazonBedrockAgent bedrockAgent,
        IAmazonRDSDataService rdsData,
        IConfiguration configuration,
        ILogger<KbSyncService> logger)
    {
        _s3 = s3;
        _bedrockAgent = bedrockAgent;
        _rdsData = rdsData;
        _logger = logger;
        _kbSourceBucket = configuration["KB_SOURCE_BUCKET"]
            ?? throw new InvalidOperationException("KB_SOURCE_BUCKET is not set.");
        _knowledgeBaseId = configuration["KNOWLEDGE_BASE_ID"]
            ?? throw new InvalidOperationException("KNOWLEDGE_BASE_ID is not set.");
        _dataSourceId = configuration["DATA_SOURCE_ID"]
            ?? throw new InvalidOperationException("DATA_SOURCE_ID is not set.");
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    public async Task<bool> ContentExistsAsync(
        string tenantId, string documentoId, int versionNumero, CancellationToken cancellationToken = default)
    {
        var key = RedactedContentKey(tenantId, documentoId, versionNumero);

        try
        {
            await _s3.GetObjectMetadataAsync(_kbSourceBucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Redacted content not found at {Bucket}/{Key}", _kbSourceBucket, key);
            return false;
        }
    }

    public async Task<string> StartIngestionJobAsync(CancellationToken cancellationToken = default)
    {
        // StartIngestionJob syncs the WHOLE data source (Bedrock has no
        // per-object ingestion API) — see ADR-011's note ⑨ on this and the
        // implementation plan's flag about calling this once per approved
        // document not necessarily scaling well at volume.
        var response = await _bedrockAgent.StartIngestionJobAsync(new StartIngestionJobRequest
        {
            KnowledgeBaseId = _knowledgeBaseId,
            DataSourceId = _dataSourceId
        }, cancellationToken);

        _logger.LogInformation("Started Bedrock KB ingestion job {IngestionJobId}", response.IngestionJob.IngestionJobId);

        return response.IngestionJob.IngestionJobId;
    }

    public async Task<string> GetIngestionJobStatusAsync(string ingestionJobId, CancellationToken cancellationToken = default)
    {
        var response = await _bedrockAgent.GetIngestionJobAsync(new GetIngestionJobRequest
        {
            KnowledgeBaseId = _knowledgeBaseId,
            DataSourceId = _dataSourceId,
            IngestionJobId = ingestionJobId
        }, cancellationToken);

        return response.IngestionJob.Status.Value;
    }

    public async Task WriteIndexedAsync(string documentoId, int versionNumero, CancellationToken cancellationToken = default) =>
        await SetEstadoAsync(documentoId, versionNumero, "INDEXED", cancellationToken);

    public async Task WriteFailedAsync(string documentoId, int versionNumero, CancellationToken cancellationToken = default) =>
        await SetEstadoAsync(documentoId, versionNumero, "FAILED", cancellationToken);

    // Must match the key convention DetectAndRedactPII writes to in
    // Services/Pii — {tenantId}/documents/{documentoId}/v{n}.txt.
    private static string RedactedContentKey(string tenantId, string documentoId, int versionNumero) =>
        $"{tenantId}/documents/{documentoId}/v{versionNumero}.txt";

    private async Task SetEstadoAsync(string documentoId, int versionNumero, string estado, CancellationToken cancellationToken)
    {
        await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "UPDATE documento_versiones SET estado = :estado WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero",
            Parameters =
            [
                new SqlParameter { Name = "estado", Value = new Field { StringValue = estado } },
                new SqlParameter { Name = "documentoId", Value = new Field { StringValue = documentoId } },
                new SqlParameter { Name = "versionNumero", Value = new Field { LongValue = versionNumero } }
            ]
        }, cancellationToken);
    }
}
