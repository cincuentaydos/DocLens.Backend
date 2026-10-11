using System.Globalization;
using System.Text.Json;

namespace DocLens.Lambda.Services.Semantic;

// Turns a Bedrock (Anthropic Messages API) InvokeModel response body into a
// flat field map. Tolerates what the model actually returns in practice:
// markdown code fences, prose around the JSON object, and non-string values
// (numbers, booleans, arrays, nested objects, null).
public static class BedrockResponseParser
{
    public static Dictionary<string, string> Parse(Stream responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        return ParseModelText(ExtractText(doc.RootElement));
    }

    public static Dictionary<string, string> ParseModelText(string? modelText)
    {
        var json = ExtractJsonObject(modelText);
        if (json is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>();
            }

            var fields = new Dictionary<string, string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                fields[property.Name] = ToFieldValue(property.Value);
            }

            return fields;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    // Concatenates every "text" content block (the Messages API can return several).
    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var texts = content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object
                            && block.TryGetProperty("text", out var t)
                            && t.ValueKind == JsonValueKind.String)
            .Select(block => block.GetProperty("text").GetString());

        return string.Concat(texts);
    }

    // Takes the outermost {...} span, which skips ```json fences and any
    // leading/trailing prose in one step.
    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string ToFieldValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString.ToLower(CultureInfo.InvariantCulture),
        JsonValueKind.False => bool.FalseString.ToLower(CultureInfo.InvariantCulture),
        // Arrays of scalars read naturally as a comma-separated list; anything
        // more complex is kept as compact JSON so no information is lost.
        JsonValueKind.Array when value.EnumerateArray().All(IsScalar) =>
            string.Join(", ", value.EnumerateArray().Select(ToFieldValue)),
        _ => value.GetRawText()
    };

    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
}
