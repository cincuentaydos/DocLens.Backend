using System.Text.RegularExpressions;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DocLens.Lambda.Tests.Data;

// Guards the SQL against the schema in DocLens.Infra/migrations/002_documents.sql:
// English table/column names, every :parameter bound, tenant filter present.
public partial class AuroraDocumentRepositoryTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid DocumentId = Guid.Parse("3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00");
    private static readonly string Key = $"tenant-a/documents/{DocumentId}/v2.pdf";

    private static readonly string[] LegacySpanishNames =
        ["documentos", "documento_versiones", "empresa_id", "estado", "creado_en", "actualizado_en", "version_numero", "documento_id"];

    private readonly IAmazonRDSDataService _rdsData = Substitute.For<IAmazonRDSDataService>();
    private readonly List<ExecuteStatementRequest> _statements = [];

    public AuroraDocumentRepositoryTests()
    {
        _rdsData.BeginTransactionAsync(Arg.Any<BeginTransactionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BeginTransactionResponse { TransactionId = "tx-1" });
        _rdsData.ExecuteStatementAsync(Arg.Do<ExecuteStatementRequest>(_statements.Add), Arg.Any<CancellationToken>())
            .Returns(call => Respond(call.Arg<ExecuteStatementRequest>()));
    }

    private static ExecuteStatementResponse Respond(ExecuteStatementRequest request)
    {
        if (request.Sql.Contains("RETURNING id::text"))
            return Records(new Field { StringValue = DocumentId.ToString() });
        if (request.Sql.Contains("RETURNING latest_version"))
            return Records(new Field { LongValue = 2 });
        if (request.Sql.TrimStart().StartsWith("SELECT"))
            return Records(new Field { StringValue = DocumentVersionStatus.Clean });
        return new ExecuteStatementResponse { NumberOfRecordsUpdated = 1, Records = [] };
    }

    private static ExecuteStatementResponse Records(Field field) => new() { Records = [[field]] };

    private AuroraDocumentRepository CreateSut() => new(
        _rdsData,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AURORA_CLUSTER"] = "arn:aws:rds:eu-west-1:000000000000:cluster:test",
            ["AURORA_SECRET"] = "arn:aws:secretsmanager:eu-west-1:000000000000:secret:test"
        }).Build(),
        NullLogger<AuroraDocumentRepository>.Instance);

    [Fact]
    public async Task AllStatements_UseTheEnglishSchema_AndBindEveryParameter()
    {
        var sut = CreateSut();
        await sut.CreateDocumentAsync(TenantId, "application/pdf", (id, n) => $"tenant-a/documents/{id}/v{n}.pdf");
        await sut.AddVersionAsync(TenantId, DocumentId, "application/pdf", (id, n) => $"tenant-a/documents/{id}/v{n}.pdf");
        await sut.GetVersionStatusAsync(TenantId, DocumentId, Key);
        await sut.TransitionVersionStatusAsync(TenantId, DocumentId, Key, DocumentVersionStatus.Pending, DocumentVersionStatus.Clean);

        Assert.Equal(6, _statements.Count);
        foreach (var statement in _statements)
        {
            foreach (var legacy in LegacySpanishNames)
                Assert.DoesNotMatch($@"\b{legacy}\b", statement.Sql);

            Assert.Matches(@"\b(documents|document_versions)\b", statement.Sql);

            var placeholders = ParameterPattern().Matches(statement.Sql).Select(m => m.Groups[1].Value).ToHashSet();
            var bound = statement.Parameters.Select(p => p.Name).ToHashSet();
            Assert.True(placeholders.SetEquals(bound),
                $"Placeholders [{string.Join(", ", placeholders)}] != bound [{string.Join(", ", bound)}] in: {statement.Sql}");
        }

        // Every statement that reads or updates existing rows is tenant-scoped.
        Assert.All(
            _statements.Where(s => !s.Sql.TrimStart().StartsWith("INSERT INTO document_versions")),
            s => Assert.Contains("tenant_id", s.Sql));
    }

    [Fact]
    public async Task NewVersions_AreInsertedAsPending()
    {
        await CreateSut().CreateDocumentAsync(TenantId, "application/pdf", (id, n) => $"tenant-a/documents/{id}/v{n}.pdf");

        var insert = Assert.Single(_statements, s => s.Sql.Contains("INSERT INTO document_versions"));
        Assert.Contains($"'{DocumentVersionStatus.Pending}'", insert.Sql);
    }

    // :name placeholders, ignoring ::type casts.
    [GeneratedRegex(@"(?<!:):([A-Za-z]\w*)")]
    private static partial Regex ParameterPattern();
}
