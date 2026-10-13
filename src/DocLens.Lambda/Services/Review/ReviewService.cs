using System.Text.Json;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

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

    public async Task<bool> ResolveAsync(
        string documentoId,
        int versionNumero,
        bool approved,
        IReadOnlyDictionary<string, string>? correctedFields,
        string? comentario,
        string? revisorId,
        CancellationToken cancellationToken = default)
    {
        var taskToken = await FindPendingTaskTokenAsync(documentoId, versionNumero, cancellationToken);
        if (taskToken is null)
        {
            _logger.LogWarning(
                "No pending review found for documento {DocumentoId} v{VersionNumero}", documentoId, versionNumero);
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

    private async Task<string?> FindPendingTaskTokenAsync(
        string documentoId, int versionNumero, CancellationToken cancellationToken)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = """
                SELECT task_token FROM document_reviews
                WHERE documento_id = :documentoId::uuid AND version_numero = :versionNumero AND resuelto_en IS NULL
                """,
            Parameters = [Param("documentoId", documentoId), ParamLong("versionNumero", versionNumero)]
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
