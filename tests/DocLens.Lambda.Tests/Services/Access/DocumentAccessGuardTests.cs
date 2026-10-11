using DocLens.Lambda.Context;
using DocLens.Lambda.Data;
using DocLens.Lambda.Models;
using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Services.Access;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DocLens.Lambda.Tests.Services.Access;

public class DocumentAccessGuardTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid DocumentId = Guid.Parse("3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00");
    private static readonly string OwnKey = DocumentKey.Build(TenantId, DocumentId, 1, "pdf");

    private readonly IDocumentRepository _repository = Substitute.For<IDocumentRepository>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();

    public DocumentAccessGuardTests() => _tenantContext.TenantId.Returns(TenantId);

    private DocumentAccessGuard CreateSut() => new(_repository, _tenantContext, NullLogger<DocumentAccessGuard>.Instance);

    private static ProcessDocumentRequest Request(string documentId, string s3Key) =>
        new(documentId, s3Key, DocumentType.Invoice);

    [Fact]
    public async Task InvalidDocumentId_IsRejected_WithoutHittingTheDatabase()
    {
        var decision = await CreateSut().CheckProcessAccessAsync(Request("doc-123", OwnKey));

        Assert.Equal(DocumentAccessDecision.InvalidDocumentId, decision);
        await _repository.DidNotReceiveWithAnyArgs().GetVersionStatusAsync(default!, default, default!);
    }

    [Theory]
    [InlineData("tenant-b/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf")] // other tenant
    [InlineData("tenant-a/documents/aaaaaaaa-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf")] // other document
    [InlineData("tenant-a/2026/01/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00.pdf")]       // legacy/arbitrary key
    [InlineData("tenant-ab/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf")] // prefix-collision tenant
    public async Task KeyOutsideCallersPrefix_IsForbidden(string s3Key)
    {
        var decision = await CreateSut().CheckProcessAccessAsync(Request(DocumentId.ToString(), s3Key));

        Assert.Equal(DocumentAccessDecision.Forbidden, decision);
        await _repository.DidNotReceiveWithAnyArgs().GetVersionStatusAsync(default!, default, default!);
    }

    [Fact]
    public async Task UnknownVersion_IsNotFound()
    {
        _repository.GetVersionStatusAsync(TenantId, DocumentId, OwnKey, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var decision = await CreateSut().CheckProcessAccessAsync(Request(DocumentId.ToString(), OwnKey));

        Assert.Equal(DocumentAccessDecision.NotFound, decision);
    }

    [Fact]
    public async Task PendingVersion_IsNotScanned()
    {
        _repository.GetVersionStatusAsync(TenantId, DocumentId, OwnKey, Arg.Any<CancellationToken>())
            .Returns(DocumentVersionStatus.Pending);

        var decision = await CreateSut().CheckProcessAccessAsync(Request(DocumentId.ToString(), OwnKey));

        Assert.Equal(DocumentAccessDecision.NotScanned, decision);
    }

    [Theory]
    [InlineData(DocumentVersionStatus.Clean)]
    [InlineData(DocumentVersionStatus.Processed)]
    public async Task CleanVersion_OwnedByCaller_IsAllowed(string status)
    {
        _repository.GetVersionStatusAsync(TenantId, DocumentId, OwnKey, Arg.Any<CancellationToken>())
            .Returns(status);

        var decision = await CreateSut().CheckProcessAccessAsync(Request(DocumentId.ToString(), OwnKey));

        Assert.Equal(DocumentAccessDecision.Allowed, decision);
    }
}
