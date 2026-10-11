using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;
using DocLens.Lambda.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DocLens.Lambda.Tests.Workers;

public class ProcessorHandlerTests
{
    private const string Bucket = "doclens-documents-test";
    private static readonly Guid DocumentId = Guid.Parse("3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00");
    private static readonly string Key = DocumentKey.Build("tenant-a", DocumentId, 2, "pdf");

    private readonly IDocumentRepository _repository = Substitute.For<IDocumentRepository>();

    private ProcessorHandler CreateSut() => new(
        _repository,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DOCUMENT_BUCKET"] = Bucket }).Build(),
        NullLogger<ProcessorHandler>.Instance);

    private static string ScanEvent(string key, string status = "NO_THREATS_FOUND", string bucket = Bucket) => $$"""
        {
          "source": "aws.guardduty",
          "detail-type": "GuardDuty Malware Protection Object Scan Result",
          "detail": {
            "s3ObjectDetails": { "bucketName": "{{bucket}}", "objectKey": "{{key}}", "versionId": "abc" },
            "scanResultDetails": { "scanResultStatus": "{{status}}" }
          }
        }
        """;

    private static SQSEvent Batch(params string[] bodies) => new()
    {
        Records = bodies.Select((body, i) => new SQSEvent.SQSMessage { MessageId = $"m{i}", Body = body }).ToList()
    };

    [Fact]
    public async Task CleanScan_MarksPendingVersionClean()
    {
        _repository.TransitionVersionStatusAsync("tenant-a", DocumentId, Key, DocumentVersionStatus.Pending, DocumentVersionStatus.Clean, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await CreateSut().HandleAsync(Batch(ScanEvent(Key)), Substitute.For<ILambdaContext>());

        Assert.Empty(response.BatchItemFailures);
        await _repository.Received(1).TransitionVersionStatusAsync(
            "tenant-a", DocumentId, Key, DocumentVersionStatus.Pending, DocumentVersionStatus.Clean, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MalformedMessage_IsReportedAsBatchItemFailure_OthersStillProcessed()
    {
        var response = await CreateSut().HandleAsync(Batch("not json", ScanEvent(Key)), Substitute.For<ILambdaContext>());

        Assert.Equal("m0", Assert.Single(response.BatchItemFailures).ItemIdentifier);
        await _repository.ReceivedWithAnyArgs(1).TransitionVersionStatusAsync(default!, default, default!, default!, default!);
    }

    [Theory]
    [InlineData("tenant-a/random-object.txt", "NO_THREATS_FOUND", Bucket)]
    [InlineData("tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v2.pdf", "THREATS_FOUND", Bucket)]
    [InlineData("tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v2.pdf", "NO_THREATS_FOUND", "other-bucket")]
    public async Task IrrelevantScanResults_AreAcknowledged_WithoutUpdates(string key, string status, string bucket)
    {
        var response = await CreateSut().HandleAsync(Batch(ScanEvent(key, status, bucket)), Substitute.For<ILambdaContext>());

        Assert.Empty(response.BatchItemFailures);
        await _repository.DidNotReceiveWithAnyArgs().TransitionVersionStatusAsync(default!, default, default!, default!, default!);
    }

    [Fact]
    public async Task RepositoryFailure_IsRetried()
    {
        _repository.TransitionVersionStatusAsync(default!, default, default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException<bool>(new TimeoutException()));

        var response = await CreateSut().HandleAsync(Batch(ScanEvent(Key)), Substitute.For<ILambdaContext>());

        Assert.Equal("m0", Assert.Single(response.BatchItemFailures).ItemIdentifier);
    }
}
