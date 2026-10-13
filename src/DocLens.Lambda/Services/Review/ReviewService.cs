using System.Text.Json;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using DocLens.Lambda.Models.Responses;

namespace DocLens.Lambda.Services.Review;

public class ReviewService : IReviewService
{
    private const string Database = "doclens";

    // camelCase to match the ASL's "$.review.approved" JSONPath (case
    // sensitive) in step_functions.tf's doclens-document-review definition.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonRDSDataService _rdsData;
    private readonly IAmazonStepFunctions _stepFunctions;
    private readonly ILogger<ReviewService> _logger;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;

    public ReviewService(
        IAmazonRDSDataService rdsData,
        IAmazonStepFunctions stepFunctions,
        IConfiguration configuration,
        ILogger<ReviewService> logger)
    {
        _rdsData = rdsData;
        _stepFunctions = stepFunctions;
        _logger = logger;
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
    }

    public async Task EnterReviewAsync(
        string documentoId, int versionNumero, string taskToken, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            """
            INSERT INTO document_reviews (documento_id, version_numero, task_token)
            VALUES (:documentoId::uuid, :versionNumero, :taskToken)
            """,
            [
                Param("documentoId", documentoId),
                ParamLong("versionNumero", versionNumero),
                Param("taskToken", taskToken)
            ],
            cancellationToken);

        _logger.LogInformation("Entered review for documento {DocumentoId} v{VersionNumero}", documentoId, versionNumero);
    }

    public async Task WriteApprovedAsync(
        string documentoId, int versionNumero, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "UPDATE documento_versiones SET estado = 'APPROVED' WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero",
            [Param("documentoId", documentoId), ParamLong("versionNumero", versionNumero)],
            cancellationToken);
    }

    public async Task WriteNeedsCorrectionAsync(
        string documentoId, int versionNumero, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "UPDATE documento_versiones SET estado = 'NEEDS_CORRECTION' WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero",
            [Param("documentoId", documentoId), ParamLong("versionNumero", versionNumero)],
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingReviewItem>> ListPendingAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = """
                SELECT r.documento_id::text, r.version_numero, v.estado, v.campos, v.content_type, v.creado_en, r.creado_en,
                       v.tipo_documento,
                       (SELECT json_agg(json_build_object('marcador', p.placeholder, 'tipo', p.tipo_entidad, 'valor', p.valor_real) ORDER BY p.creado_en)
                 FROM pii_mappings p WHERE p.documento_id = v.documento_id)::text
                FROM document_reviews r
                JOIN documento_versiones v ON v.documento_id = r.documento_id AND v.version_numero = r.version_numero
                JOIN documentos d ON d.id = r.documento_id
                WHERE r.resuelto_en IS NULL AND d.empresa_id = :empresaId
                ORDER BY r.creado_en
                """,
            Parameters = [Param("empresaId", tenantId)]
        }, cancellationToken);

        return response.Records.Select(record => new PendingReviewItem
        {
            DocumentId = record[0].StringValue!,
            VersionNumber = (int)record[1].LongValue!.Value,
            Estado = record[2].StringValue!,
            // Data API omits isNull on a non-null value, so test the value
            // itself — `IsNull ?? true` would read every row as null.
            Campos = record[3].StringValue is null
                ? null
                : JsonDocument.Parse(record[3].StringValue!).RootElement,
            ContentType = record[4].StringValue!,
            SubidoEn = record[5].StringValue!,
            EnRevisionDesde = record[6].StringValue!,
            TipoDocumento = record[7].StringValue,
            Pii = record[8].StringValue is null
                ? null
                : JsonDocument.Parse(record[8].StringValue!).RootElement
        }).ToList();
    }

    public async Task<bool> ResolveAsync(
        string tenantId,
        string documentoId,
        int versionNumero,
        bool approved,
        IReadOnlyDictionary<string, string>? correctedFields,
        string? comentario,
        string? revisorId,
        CancellationToken cancellationToken = default)
    {
        var taskToken = await FindPendingTaskTokenAsync(tenantId, documentoId, versionNumero, cancellationToken);
        if (taskToken is null)
        {
            _logger.LogWarning(
                "No pending review found for documento {DocumentoId} v{VersionNumero}, tenant {TenantId}",
                documentoId, versionNumero, tenantId);
            return false;
        }

        var output = JsonSerializer.Serialize(new
        {
            Approved = approved,
            CorrectedFields = correctedFields,
            Comentario = comentario
        }, JsonOptions);

        await _stepFunctions.SendTaskSuccessAsync(new SendTaskSuccessRequest
        {
            TaskToken = taskToken,
            Output = output
        }, cancellationToken);

        await ExecuteAsync(
            """
            UPDATE document_reviews
            SET revisor_id = :revisorId, decision = :decision, comentario = :comentario, resuelto_en = now()
            WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero AND resuelto_en IS NULL
            """,
            [
                Param("revisorId", revisorId),
                Param("decision", approved ? "APPROVED" : "NEEDS_CORRECTION"),
                Param("comentario", comentario),
                Param("documentoId", documentoId),
                ParamLong("versionNumero", versionNumero)
            ],
            cancellationToken);

        _logger.LogInformation(
            "Resolved review for documento {DocumentoId} v{VersionNumero}: approved={Approved}",
            documentoId, versionNumero, approved);

        return true;
    }

    // Joins documentos so a caller can only resolve their own tenant's
    // reviews — the documentoId alone comes straight off the URL.
    private async Task<string?> FindPendingTaskTokenAsync(
        string tenantId, string documentoId, int versionNumero, CancellationToken cancellationToken)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = """
                SELECT r.task_token FROM document_reviews r
                JOIN documentos d ON d.id = r.documento_id
                WHERE r.documento_id = :documentoId::uuid AND r.version_numero = :versionNumero
                  AND r.resuelto_en IS NULL AND d.empresa_id = :empresaId
                """,
            Parameters =
            [
                Param("documentoId", documentoId),
                ParamLong("versionNumero", versionNumero),
                Param("empresaId", tenantId)
            ]
        }, cancellationToken);

        return response.Records.Count == 0 ? null : response.Records[0][0].StringValue;
    }

    private async Task ExecuteAsync(string sql, List<SqlParameter> parameters, CancellationToken cancellationToken)
    {
        await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = sql,
            Parameters = parameters
        }, cancellationToken);
    }

    private static SqlParameter Param(string name, string? value) =>
        new() { Name = name, Value = value is null ? new Field { IsNull = true } : new Field { StringValue = value } };

    private static SqlParameter ParamLong(string name, long value) =>
        new() { Name = name, Value = new Field { LongValue = value } };
}
