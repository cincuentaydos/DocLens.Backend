using Amazon.S3;
using Amazon.S3.Model;
using DocLens.Lambda.Context;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Services.Upload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DocLens.Lambda.Tests.Services.Upload;

public class DocumentUploadServiceTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid DocumentId = Guid.Parse("3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00");

    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly IDocumentRepository _repository = Substitute.For<IDocumentRepository>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();

    public DocumentUploadServiceTests()
    {
        _tenantContext.TenantId.Returns(TenantId);
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://signed.example/upload");
    }

    private DocumentUploadService CreateSut() => new(
        _s3,
        _repository,
        _tenantContext,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DOCUMENT_BUCKET"] = "doclens-documents-test"
        }).Build(),
        NullLogger<DocumentUploadService>.Instance);

    [Fact]
    public async Task NewDocument_CreatesVersionOne_UnderTenantPrefix()
    {
        _repository.CreateDocumentAsync(TenantId, "application/pdf", Arg.Any<Func<Guid, int, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocumentVersionRecord(DocumentId, 1, call.Arg<Func<Guid, int, string>>()(DocumentId, 1)));

        var result = await CreateSut().PrepareUploadAsync(new PrepareDocumentUploadRequest("application/pdf", null));

        Assert.Equal(DocumentId.ToString(), result.DocumentId);
        Assert.Equal(1, result.VersionNumber);
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Key == $"tenant-a/documents/{DocumentId}/v1.pdf" && r.Verb == HttpVerb.PUT));
    }

    [Fact]
    public async Task ExistingDocument_UsesTenantScopedAtomicIncrement()
    {
        _repository.AddVersionAsync(TenantId, DocumentId, "image/png", Arg.Any<Func<Guid, int, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocumentVersionRecord(DocumentId, 4, call.Arg<Func<Guid, int, string>>()(DocumentId, 4)));

        var result = await CreateSut().PrepareUploadAsync(new PrepareDocumentUploadRequest("image/png", DocumentId.ToString()));

        Assert.Equal(4, result.VersionNumber);
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r => r.Key == $"tenant-a/documents/{DocumentId}/v4.png"));
    }

    [Fact]
    public async Task ExistingDocument_OfAnotherTenant_IsNotFound_AndNothingIsSigned()
    {
        _repository.AddVersionAsync(TenantId, DocumentId, "application/pdf", Arg.Any<Func<Guid, int, string>>(), Arg.Any<CancellationToken>())
            .Returns((DocumentVersionRecord?)null);

        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            CreateSut().PrepareUploadAsync(new PrepareDocumentUploadRequest("application/pdf", DocumentId.ToString())));
        _s3.DidNotReceiveWithAnyArgs().GetPreSignedURL(default!);
    }

    [Fact]
    public async Task NonUuidDocumentId_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidDocumentIdException>(() =>
            CreateSut().PrepareUploadAsync(new PrepareDocumentUploadRequest("application/pdf", "doc-123")));
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, default, default!, default!);
    }

    [Fact]
    public async Task UnsupportedContentType_IsRejected()
    {
        await Assert.ThrowsAsync<UnsupportedContentTypeException>(() =>
            CreateSut().PrepareUploadAsync(new PrepareDocumentUploadRequest("application/x-msdownload", null)));
    }
}
