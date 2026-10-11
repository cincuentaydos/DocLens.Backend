using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DocLens.Lambda.Workers;

// Textract async job completion notification (StartDocumentTextDetection),
// published to SNS and fanned out to the OCR result queue. The SQS body is
// normally the SNS envelope ({"Type":"Notification","Message":"<json>"});
// raw message delivery (no envelope) is accepted as well.
public sealed record TextractCompletion(string JobId, string Status, string? Api, string? JobTag, string? S3Bucket, string? S3ObjectName)
{
    public bool Succeeded => Status == "SUCCEEDED";

    public static bool TryParse(string? messageBody, [NotNullWhen(true)] out TextractCompletion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return false;
        }

        try
        {
            using var outer = JsonDocument.Parse(messageBody);
            var root = outer.RootElement;

            if (root.TryGetProperty("Type", out var type) && type.GetString() == "Notification"
                && root.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(message.GetString()!);
                return TryRead(inner.RootElement, out result);
            }

            return TryRead(root, out result);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRead(JsonElement root, [NotNullWhen(true)] out TextractCompletion? result)
    {
        result = null;
        var jobId = GetString(root, "JobId");
        var status = GetString(root, "Status");
        if (jobId is null || status is null)
        {
            return false;
        }

        string? bucket = null, objectName = null;
        if (root.TryGetProperty("DocumentLocation", out var location) && location.ValueKind == JsonValueKind.Object)
        {
            bucket = GetString(location, "S3Bucket");
            objectName = GetString(location, "S3ObjectName");
        }

        result = new TextractCompletion(jobId, status, GetString(root, "API"), GetString(root, "JobTag"), bucket, objectName);
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
