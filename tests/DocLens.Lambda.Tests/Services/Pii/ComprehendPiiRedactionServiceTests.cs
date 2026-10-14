using Amazon.Comprehend;
using Amazon.Comprehend.Model;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using DocLens.Lambda.Services.Pii;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DocLens.Lambda.Tests.Services.Pii;

public class ComprehendPiiRedactionServiceTests
{
    private readonly IAmazonComprehend _comprehend = Substitute.For<IAmazonComprehend>();
    private readonly IAmazonRDSDataService _rdsData = Substitute.For<IAmazonRDSDataService>();
    private readonly ComprehendPiiRedactionService _sut;

    public ComprehendPiiRedactionServiceTests()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["AURORA_CLUSTER"].Returns("arn:aws:rds:eu-west-1:123456789012:cluster:test");
        configuration["AURORA_SECRET"].Returns("arn:aws:secretsmanager:eu-west-1:123456789012:secret:test");

        _sut = new ComprehendPiiRedactionService(
            _comprehend, _rdsData, configuration, Substitute.For<ILogger<ComprehendPiiRedactionService>>());
    }

    [Fact]
    public async Task RedactAsync_ReplacesDetectedNameWithStablePlaceholder()
    {
        const string text = "Contrato entre Juan Perez y la empresa.";
        var nameStart = text.IndexOf("Juan Perez", StringComparison.Ordinal);

        _comprehend.DetectPiiEntitiesAsync(Arg.Any<DetectPiiEntitiesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DetectPiiEntitiesResponse
            {
                Entities =
                [
                    new PiiEntity
                    {
                        Type = PiiEntityType.NAME,
                        Score = 0.99f,
                        BeginOffset = nameStart,
                        EndOffset = nameStart + "Juan Perez".Length
                    }
                ]
            });

        var result = await _sut.RedactAsync("11111111-1111-1111-1111-111111111111", text);

        Assert.Equal(1, result.EntitiesFound);
        Assert.DoesNotContain("Juan Perez", result.RedactedText);
        Assert.Contains("[PARTE_A]", result.RedactedText);
        Assert.Equal("Contrato entre [PARTE_A] y la empresa.", result.RedactedText);

        await _rdsData.Received(1).BatchExecuteStatementAsync(
            Arg.Is<BatchExecuteStatementRequest>(r => r.ParameterSets.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedactAsync_AssignsDifferentLettersToDifferentNames()
    {
        const string text = "Entre Maria Lopez y Pedro Ruiz.";
        var mariaStart = text.IndexOf("Maria Lopez", StringComparison.Ordinal);
        var pedroStart = text.IndexOf("Pedro Ruiz", StringComparison.Ordinal);

        _comprehend.DetectPiiEntitiesAsync(Arg.Any<DetectPiiEntitiesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DetectPiiEntitiesResponse
            {
                Entities =
                [
                    new PiiEntity { Type = PiiEntityType.NAME, Score = 0.99f, BeginOffset = mariaStart, EndOffset = mariaStart + "Maria Lopez".Length },
                    new PiiEntity { Type = PiiEntityType.NAME, Score = 0.99f, BeginOffset = pedroStart, EndOffset = pedroStart + "Pedro Ruiz".Length }
                ]
            });

        var result = await _sut.RedactAsync("11111111-1111-1111-1111-111111111111", text);

        Assert.Equal("Entre [PARTE_A] y [PARTE_B].", result.RedactedText);
    }

    [Fact]
    public async Task RedactAsync_KeepsCompanyTaxIdTaggedAsBankAccount()
    {
        const string text = "Emisor: Bufete S.L., CIF B12345678, cuenta ES91 2100 0418 4502 0005 1332.";
        var cifStart = text.IndexOf("B12345678", StringComparison.Ordinal);
        var ibanStart = text.IndexOf("ES91", StringComparison.Ordinal);

        _comprehend.DetectPiiEntitiesAsync(Arg.Any<DetectPiiEntitiesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DetectPiiEntitiesResponse
            {
                Entities =
                [
                    new PiiEntity { Type = PiiEntityType.BANK_ACCOUNT_NUMBER, Score = 0.9f, BeginOffset = cifStart, EndOffset = cifStart + "B12345678".Length },
                    new PiiEntity { Type = PiiEntityType.INTERNATIONAL_BANK_ACCOUNT_NUMBER, Score = 0.99f, BeginOffset = ibanStart, EndOffset = ibanStart + "ES91 2100 0418 4502 0005 1332".Length }
                ]
            });

        var result = await _sut.RedactAsync("11111111-1111-1111-1111-111111111111", text);

        Assert.Equal("Emisor: Bufete S.L., CIF B12345678, cuenta [DATO_1].", result.RedactedText);
        Assert.Equal(1, result.EntitiesFound);
    }

    [Fact]
    public async Task RedactAsync_StillRedactsPersonalDni()
    {
        const string text = "DNI 12345678Z del arrendatario.";

        _comprehend.DetectPiiEntitiesAsync(Arg.Any<DetectPiiEntitiesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DetectPiiEntitiesResponse
            {
                Entities = [new PiiEntity { Type = PiiEntityType.BANK_ACCOUNT_NUMBER, Score = 0.9f, BeginOffset = 4, EndOffset = 13 }]
            });

        var result = await _sut.RedactAsync("11111111-1111-1111-1111-111111111111", text);

        Assert.Equal("DNI [CUENTA_1] del arrendatario.", result.RedactedText);
    }

    [Fact]
    public async Task RedactAsync_IgnoresEntitiesBelowConfidenceThreshold()
    {
        const string text = "Fecha probable: quizas manana.";

        _comprehend.DetectPiiEntitiesAsync(Arg.Any<DetectPiiEntitiesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DetectPiiEntitiesResponse
            {
                Entities = [new PiiEntity { Type = PiiEntityType.DATE_TIME, Score = 0.1f, BeginOffset = 0, EndOffset = 5 }]
            });

        var result = await _sut.RedactAsync("11111111-1111-1111-1111-111111111111", text);

        Assert.Equal(0, result.EntitiesFound);
        Assert.Equal(text, result.RedactedText);
        await _rdsData.DidNotReceive().BatchExecuteStatementAsync(Arg.Any<BatchExecuteStatementRequest>(), Arg.Any<CancellationToken>());
    }
}
