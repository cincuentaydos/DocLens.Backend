using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.Textract;
using Amazon.Textract.Model;

namespace DocLens.Lambda.Services.Ocr;

public class TextractOcrService : IOcrService
{
    private const string Database = "doclens";

    private readonly IAmazonTextract _textract;
    private readonly IAmazonRDSDataService _rdsData;
    private readonly ILogger<TextractOcrService> _logger;
    private readonly string _documentBucket;
    private readonly string _auroraClusterArn;
    private readonly string _auroraSecretArn;
    private readonly string _textractSnsTopicArn;
    private readonly string _textractSnsRoleArn;

    public TextractOcrService(
        IAmazonTextract textract,
        IAmazonRDSDataService rdsData,
        IConfiguration configuration,
        ILogger<TextractOcrService> logger)
    {
        _textract = textract;
        _rdsData = rdsData;
        _logger = logger;
        _documentBucket = configuration["DOCUMENT_BUCKET"]
            ?? throw new InvalidOperationException("DOCUMENT_BUCKET is not set.");
        _auroraClusterArn = configuration["AURORA_CLUSTER"]
            ?? throw new InvalidOperationException("AURORA_CLUSTER is not set.");
        _auroraSecretArn = configuration["AURORA_SECRET"]
            ?? throw new InvalidOperationException("AURORA_SECRET is not set.");
        _textractSnsTopicArn = configuration["TEXTRACT_SNS_TOPIC_ARN"]
            ?? throw new InvalidOperationException("TEXTRACT_SNS_TOPIC_ARN is not set.");
        _textractSnsRoleArn = configuration["TEXTRACT_SNS_ROLE_ARN"]
            ?? throw new InvalidOperationException("TEXTRACT_SNS_ROLE_ARN is not set.");
    }

    public async Task<string> ExtractTextAsync(string s3Key, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting sync OCR for {S3Key}", s3Key);

        var response = await _textract.DetectDocumentTextAsync(new DetectDocumentTextRequest
        {
            Document = new Document
            {
                S3Object = new S3Object { Bucket = _documentBucket, Name = s3Key }
            }
        }, cancellationToken);

        var text = JoinLines(response.Blocks);

        _logger.LogInformation("Sync OCR completed for {S3Key}, extracted {CharCount} chars", s3Key, text.Length);

        return text;
    }

    public async Task<string> StartAsync(
        string s3Key,
        string tenantId,
        string documentoId,
        int versionNumero,
        string taskToken,
        CancellationToken cancellationToken = default)
    {
        var jobTag = await InsertJobCorrelationAsync(tenantId, documentoId, versionNumero, taskToken, cancellationToken);

        _logger.LogInformation(
            "Starting async Textract job for {S3Key}, documento {DocumentoId} v{VersionNumero}, jobTag {JobTag}",
            s3Key, documentoId, versionNumero, jobTag);

        await _textract.StartDocumentTextDetectionAsync(new StartDocumentTextDetectionRequest
        {
            DocumentLocation = new DocumentLocation
            {
                S3Object = new S3Object { Bucket = _documentBucket, Name = s3Key }
            },
            JobTag = jobTag,
            NotificationChannel = new NotificationChannel
            {
                SNSTopicArn = _textractSnsTopicArn,
                RoleArn = _textractSnsRoleArn
            }
        }, cancellationToken);

        return jobTag;
    }

    public async Task<TextractJobCorrelation> ResolveJobTagAsync(string jobTag, CancellationToken cancellationToken = default)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = "SELECT task_token, tenant_id, documento_id, version_numero FROM textract_jobs WHERE job_tag = :jobTag::uuid",
            Parameters = [new SqlParameter { Name = "jobTag", Value = new Field { StringValue = jobTag } }]
        }, cancellationToken);

        if (response.Records.Count == 0)
        {
            throw new InvalidOperationException($"No textract_jobs row found for job tag {jobTag}.");
        }

        var record = response.Records[0];
        return new TextractJobCorrelation(
            TaskToken: record[0].StringValue!,
            TenantId: record[1].StringValue!,
            DocumentoId: record[2].StringValue!,
            VersionNumero: (int)record[3].LongValue!.Value);
    }

    public async Task<string> GetResultAsync(string textractJobId, CancellationToken cancellationToken = default)
    {
        var blocks = new List<Block>();
        string? nextToken = null;

        do
        {
            var response = await _textract.GetDocumentTextDetectionAsync(new GetDocumentTextDetectionRequest
            {
                JobId = textractJobId,
                NextToken = nextToken
            }, cancellationToken);

            blocks.AddRange(response.Blocks);
            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var text = JoinLines(blocks);

        _logger.LogInformation("Async OCR result fetched for job {JobId}, {CharCount} chars", textractJobId, text.Length);

        return text;
    }

    private async Task<string> InsertJobCorrelationAsync(
        string tenantId, string documentoId, int versionNumero, string taskToken, CancellationToken cancellationToken)
    {
        var response = await _rdsData.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            ResourceArn = _auroraClusterArn,
            SecretArn = _auroraSecretArn,
            Database = Database,
            Sql = """
                INSERT INTO textract_jobs (tenant_id, documento_id, version_numero, task_token)
                VALUES (:tenantId, :documentoId::uuid, :versionNumero, :taskToken)
                RETURNING job_tag
                """,
            Parameters =
            [
                new SqlParameter { Name = "tenantId", Value = new Field { StringValue = tenantId } },
                new SqlParameter { Name = "documentoId", Value = new Field { StringValue = documentoId } },
                new SqlParameter { Name = "versionNumero", Value = new Field { LongValue = versionNumero } },
                new SqlParameter { Name = "taskToken", Value = new Field { StringValue = taskToken } }
            ]
        }, cancellationToken);

        return response.Records[0][0].StringValue!;
    }

    private static string JoinLines(IEnumerable<Block> blocks) =>
        string.Join(" ", blocks.Where(b => b.BlockType == BlockType.LINE).Select(b => b.Text));
}
