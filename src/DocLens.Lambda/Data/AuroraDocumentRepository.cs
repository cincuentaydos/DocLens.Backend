using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace DocLens.Lambda.Data;

// RDS Data API implementation (ADR-008). Every statement is scoped by
// empresa_id so a caller can never touch another tenant's rows, and every
// multi-statement write runs inside a Data API transaction.
public class AuroraDocumentRepository : IDocumentRepository
{
    // Matches DocLens.Infra modules/data/main.tf's database_name; overridable
    // through the AURORA_DATABASE environment variable.
    private const string DefaultDatabase = "doclens";

    private readonly IAmazonRDSDataService _rdsData;
    private readonly ILogger<AuroraDocumentRepository> _logger;
    private readonly string _clusterArn;
    private readonly string _secretArn;
    private readonly string _database;

    public AuroraDocumentRepository(
        IAmazonRDSDataService rdsData,
        IConfiguration configuration,
        ILogger<AuroraDocumentRepository> logger)
    {
        _rdsData = rdsData;
        _logger = logger;
        _clusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _secretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
        _database = configuration["AURORA_DATABASE"] ?? DefaultDatabase;
    }

    public Task<DocumentVersionRecord> CreateDocumentAsync(
        string tenantId,
        string contentType,
        Func<Guid, int, string> s3KeyFactory,
        CancellationToken cancellationToken = default)
    {
        return InTransactionAsync(async transactionId =>
        {
            var inserted = await ExecuteAsync(
                "INSERT INTO documentos (empresa_id, latest_version) VALUES (:empresaId, 1) RETURNING id::text",
                [Text("empresaId", tenantId)],
                transactionId,
                cancellationToken);

            var documentId = Guid.Parse(inserted.Records[0][0].StringValue);
            var s3Key = s3KeyFactory(documentId, 1);
            await InsertVersionAsync(documentId, 1, s3Key, contentType, transactionId, cancellationToken);
            return new DocumentVersionRecord(documentId, 1, s3Key);
        }, cancellationToken);
    }

    public async Task<DocumentVersionRecord?> AddVersionAsync(
        string tenantId,
        Guid documentId,
        string contentType,
        Func<Guid, int, string> s3KeyFactory,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync<DocumentVersionRecord?>(async transactionId =>
        {
            // Single atomic read-modify-write: concurrent uploads of the same
            // document each get a distinct version number, and the row lock
            // is held until the transaction commits.
            var updated = await ExecuteAsync(
                """
                UPDATE documentos
                SET latest_version = latest_version + 1, actualizado_en = now()
                WHERE id = :id::uuid AND empresa_id = :empresaId
                RETURNING latest_version
                """,
                [Text("id", documentId.ToString()), Text("empresaId", tenantId)],
                transactionId,
                cancellationToken);

            if (updated.Records is null || updated.Records.Count == 0)
            {
                return null;
            }

            var versionNumber = checked((int)updated.Records[0][0].LongValue!.Value);
            var s3Key = s3KeyFactory(documentId, versionNumber);
            await InsertVersionAsync(documentId, versionNumber, s3Key, contentType, transactionId, cancellationToken);
            return new DocumentVersionRecord(documentId, versionNumber, s3Key);
        }, cancellationToken);
    }

    public async Task<string?> GetVersionStatusAsync(
        string tenantId,
        Guid documentId,
        string s3Key,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(
            """
            SELECT v.estado
            FROM documento_versiones v
            JOIN documentos d ON d.id = v.documento_id
            WHERE d.id = :id::uuid AND d.empresa_id = :empresaId AND v.s3_key = :s3Key
            """,
            [Text("id", documentId.ToString()), Text("empresaId", tenantId), Text("s3Key", s3Key)],
            transactionId: null,
            cancellationToken);

        return response.Records is { Count: > 0 } ? response.Records[0][0].StringValue : null;
    }

    public async Task<bool> TransitionVersionStatusAsync(
        string tenantId,
        Guid documentId,
        string s3Key,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(
            """
            UPDATE documento_versiones v
            SET estado = :toStatus, actualizado_en = now()
            FROM documentos d
            WHERE d.id = v.documento_id
              AND d.id = :id::uuid AND d.empresa_id = :empresaId
              AND v.s3_key = :s3Key AND v.estado = :fromStatus
            """,
            [
                Text("toStatus", toStatus),
                Text("id", documentId.ToString()),
                Text("empresaId", tenantId),
                Text("s3Key", s3Key),
                Text("fromStatus", fromStatus)
            ],
            transactionId: null,
            cancellationToken);

        return response.NumberOfRecordsUpdated > 0;
    }

    private Task InsertVersionAsync(
        Guid documentId, int versionNumber, string s3Key, string contentType, string transactionId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            """
            INSERT INTO documento_versiones (documento_id, version_numero, s3_key, content_type, estado)
            VALUES (:documentoId::uuid, :versionNumero, :s3Key, :contentType, 'PENDING')
            """,
            [
                Text("documentoId", documentId.ToString()),
                new SqlParameter { Name = "versionNumero", Value = new Field { LongValue = versionNumber } },
                Text("s3Key", s3Key),
                Text("contentType", contentType)
            ],
            transactionId,
            cancellationToken);
    }

    private async Task<T> InTransactionAsync<T>(Func<string, Task<T>> work, CancellationToken cancellationToken)
    {
        var transaction = await _rdsData.BeginTransactionAsync(new BeginTransactionRequest
        {
            ResourceArn = _clusterArn,
            SecretArn = _secretArn,
            Database = _database
        }, cancellationToken);

        try
        {
            var result = await work(transaction.TransactionId);
            await _rdsData.CommitTransactionAsync(new CommitTransactionRequest
            {
                ResourceArn = _clusterArn,
                SecretArn = _secretArn,
                TransactionId = transaction.TransactionId
            }, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rolling back transaction {TransactionId}", transaction.TransactionId);
            // CancellationToken.None: the rollback must still go out even when
            // the request itself was cancelled.
            try
            {
                await _rdsData.RollbackTransactionAsync(new RollbackTransactionRequest
                {
                    ResourceArn = _clusterArn,
                    SecretArn = _secretArn,
                    TransactionId = transaction.TransactionId
                }, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                // Keep the original failure as the one that surfaces; the Data
                // API also expires abandoned transactions on its own.
                _logger.LogError(rollbackEx, "Rollback of transaction {TransactionId} failed", transaction.TransactionId);
            }

            throw;
        }
    }

    private Task<ExecuteStatementResponse> ExecuteAsync(
        string sql, List<SqlParameter> parameters, string? transactionId, CancellationToken cancellationToken)
    {
        return _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _clusterArn,
            SecretArn = _secretArn,
            Database = _database,
            Sql = sql,
            Parameters = parameters,
            TransactionId = transactionId
        }, cancellationToken);
    }

    private static SqlParameter Text(string name, string value) =>
        new() { Name = name, Value = new Field { StringValue = value } };
}
