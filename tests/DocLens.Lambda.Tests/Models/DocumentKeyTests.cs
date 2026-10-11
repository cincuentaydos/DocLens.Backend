using DocLens.Lambda.Models;

namespace DocLens.Lambda.Tests.Models;

public class DocumentKeyTests
{
    private static readonly Guid DocumentId = Guid.Parse("3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00");

    [Fact]
    public void Build_And_TryParse_RoundTrip()
    {
        var key = DocumentKey.Build("tenant-a", DocumentId, 3, "pdf");

        Assert.Equal($"tenant-a/documents/{DocumentId}/v3.pdf", key);
        Assert.True(DocumentKey.TryParse(key, out var parsed));
        Assert.Equal("tenant-a", parsed.TenantId);
        Assert.Equal(DocumentId, parsed.DocumentId);
        Assert.Equal(3, parsed.VersionNumber);
        Assert.Equal("pdf", parsed.Extension);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tenant-a/2026/01/doc.pdf")]
    [InlineData("tenant-a/documents/not-a-guid/v1.pdf")]
    [InlineData("tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v0.pdf")]
    [InlineData("tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v01.pdf")]
    [InlineData("tenant-a/documents/3F2B8C1E-2D4A-4B6F-9A1C-7E5D3B2A1F00/v1.pdf")]
    [InlineData("tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf/extra")]
    [InlineData("../tenant-a/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf")]
    [InlineData("/documents/3f2b8c1e-2d4a-4b6f-9a1c-7e5d3b2a1f00/v1.pdf")]
    public void TryParse_RejectsKeysOutsideTheConvention(string? s3Key)
    {
        Assert.False(DocumentKey.TryParse(s3Key, out _));
    }
}
