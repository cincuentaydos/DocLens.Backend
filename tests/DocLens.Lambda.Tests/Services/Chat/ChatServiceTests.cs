using System.Text;
using System.Text.Json;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using DocLens.Lambda.Context;
using DocLens.Lambda.Services.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DocLens.Lambda.Tests.Services.Chat;

public class ChatServiceTests
{
    private const string DocA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string DocB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private readonly IAmazonBedrockAgentRuntime _agentRuntime = Substitute.For<IAmazonBedrockAgentRuntime>();
    private readonly IAmazonBedrockRuntime _runtime = Substitute.For<IAmazonBedrockRuntime>();
    private readonly IAmazonRDSDataService _rdsData = Substitute.For<IAmazonRDSDataService>();
    private readonly ChatService _sut;

    public ChatServiceTests()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["KNOWLEDGE_BASE_ID"].Returns("kb-test");
        configuration["CHAT_MODEL_ID"].Returns("model-test");
        configuration["AURORA_CLUSTER"].Returns("arn:aws:rds:eu-west-1:123456789012:cluster:test");
        configuration["AURORA_SECRET"].Returns("arn:aws:secretsmanager:eu-west-1:123456789012:secret:test");

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns("test-tenant");

        // Both documents use [PARTE_A] — for different people.
        _agentRuntime.RetrieveAsync(Arg.Any<RetrieveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieveResponse
            {
                RetrievalResults =
                [
                    Chunk(DocA, "Factura emitida a [PARTE_A]."),
                    Chunk(DocB, "Contrato firmado por [PARTE_A].")
                ]
            });

        _rdsData.ExecuteStatementAsync(Arg.Any<ExecuteStatementRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var documentoId = call.Arg<ExecuteStatementRequest>().Parameters[0].Value.StringValue;
                var name = documentoId == DocA ? "Laura Gomez" : "Marta Sanchez";
                return new ExecuteStatementResponse
                {
                    Records = [[new Field { StringValue = "[PARTE_A]" }, new Field { StringValue = name }]]
                };
            });

        _sut = new ChatService(
            _agentRuntime, _runtime, _rdsData, tenantContext, configuration, Substitute.For<ILogger<ChatService>>());
    }

    [Fact]
    public async Task AskAsync_ReturnsOnlyCitedDocumentsAndUnredactsWithTheirOwnMappings()
    {
        ModelAnswers("El contrato lo firmó [D2_PARTE_A].\nFUENTES: 2");

        var response = await _sut.AskAsync("Quien firmo el contrato?");

        Assert.Equal("El contrato lo firmó Marta Sanchez.", response.Respuesta);
        Assert.Equal([DocB], response.Fuentes);
    }

    [Fact]
    public async Task AskAsync_ReturnsNoSourcesWhenModelCitesNone()
    {
        ModelAnswers("El contexto no contiene esa información.\nFUENTES: ninguna");

        var response = await _sut.AskAsync("Cual es la capital de Francia?");

        Assert.Equal("El contexto no contiene esa información.", response.Respuesta);
        Assert.Empty(response.Fuentes);
    }

    private void ModelAnswers(string text)
    {
        var body = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } } });
        _runtime.InvokeModelAsync(Arg.Any<InvokeModelRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) });
    }

    private static KnowledgeBaseRetrievalResult Chunk(string documentoId, string text) => new()
    {
        Content = new RetrievalResultContent { Text = text },
        Location = new RetrievalResultLocation
        {
            S3Location = new RetrievalResultS3Location
            {
                Uri = $"s3://doclens-kb-source-test/test-tenant/documents/{documentoId}/v1.txt"
            }
        }
    };
}
