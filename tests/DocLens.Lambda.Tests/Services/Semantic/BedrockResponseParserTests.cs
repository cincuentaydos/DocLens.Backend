using System.Text;
using System.Text.Json;
using DocLens.Lambda.Services.Semantic;

namespace DocLens.Lambda.Tests.Services.Semantic;

public class BedrockResponseParserTests
{
    private static Stream Envelope(string modelText) =>
        new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = modelText } }
        })));

    [Fact]
    public void PlainJsonObject_IsParsed()
    {
        var fields = BedrockResponseParser.Parse(Envelope("""{"issuer":"ACME","total_amount":"500"}"""));

        Assert.Equal("ACME", fields["issuer"]);
        Assert.Equal("500", fields["total_amount"]);
    }

    [Fact]
    public void MarkdownFencesAndProse_AreStripped()
    {
        var text = "Here is the data:\n```json\n{\"issuer\": \"ACME\"}\n```\nLet me know!";

        var fields = BedrockResponseParser.Parse(Envelope(text));

        Assert.Equal("ACME", Assert.Single(fields).Value);
    }

    [Fact]
    public void NonStringValues_AreConverted()
    {
        var text = """
            {"total_amount": 500.25, "paid": true, "due_date": null,
             "skills": ["C#", "AWS"], "address": {"city": "Madrid"}}
            """;

        var fields = BedrockResponseParser.Parse(Envelope(text));

        Assert.Equal("500.25", fields["total_amount"]);
        Assert.Equal("true", fields["paid"]);
        Assert.Equal("", fields["due_date"]);
        Assert.Equal("C#, AWS", fields["skills"]);
        Assert.Equal("""{"city": "Madrid"}""", fields["address"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I could not find any fields.")]
    [InlineData("{ not json }")]
    public void UnparseableText_YieldsEmptyMap(string text)
    {
        Assert.Empty(BedrockResponseParser.Parse(Envelope(text)));
    }

    [Fact]
    public void MissingContent_YieldsEmptyMap()
    {
        var body = new MemoryStream(Encoding.UTF8.GetBytes("""{"type":"message"}"""));

        Assert.Empty(BedrockResponseParser.Parse(body));
    }
}
