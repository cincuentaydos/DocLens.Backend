using System.Text.Json;
using DocLens.Lambda.Workers;

namespace DocLens.Lambda.Tests.Workers;

public class TextractCompletionTests
{
    private const string Notification = """
        {"JobId":"job-1","Status":"SUCCEEDED","API":"StartDocumentTextDetection","JobTag":"doc-1",
         "DocumentLocation":{"S3ObjectName":"tenant-a/documents/x/v1.pdf","S3Bucket":"bucket"}}
        """;

    [Fact]
    public void SnsEnvelope_IsUnwrapped()
    {
        var envelope = JsonSerializer.Serialize(new { Type = "Notification", Message = Notification });

        Assert.True(TextractCompletion.TryParse(envelope, out var completion));
        Assert.Equal("job-1", completion.JobId);
        Assert.True(completion.Succeeded);
        Assert.Equal("tenant-a/documents/x/v1.pdf", completion.S3ObjectName);
    }

    [Fact]
    public void RawDelivery_IsAccepted()
    {
        Assert.True(TextractCompletion.TryParse(Notification, out var completion));
        Assert.Equal("StartDocumentTextDetection", completion.Api);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("""{"Type":"Notification","Message":"{}"}""")]
    public void InvalidBodies_AreRejected(string? body)
    {
        Assert.False(TextractCompletion.TryParse(body, out _));
    }
}
