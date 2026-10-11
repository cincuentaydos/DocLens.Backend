using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DocLens.Lambda.Workers;

// The subset of the "GuardDuty Malware Protection Object Scan Result"
// EventBridge event the Processor needs. EventBridge delivers the whole event
// as the SQS message body (modules/processing/guardduty_event.tf).
public sealed record GuardDutyScanResult(string BucketName, string ObjectKey, string? VersionId, string ScanResultStatus)
{
    public const string NoThreatsFound = "NO_THREATS_FOUND";

    public bool IsClean => ScanResultStatus == NoThreatsFound;

    public static bool TryParse(string? messageBody, [NotNullWhen(true)] out GuardDutyScanResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(messageBody);
            if (!doc.RootElement.TryGetProperty("detail", out var detail)
                || !detail.TryGetProperty("s3ObjectDetails", out var s3)
                || !detail.TryGetProperty("scanResultDetails", out var scan))
            {
                return false;
            }

            var bucket = GetString(s3, "bucketName");
            var key = GetString(s3, "objectKey");
            var status = GetString(scan, "scanResultStatus");
            if (bucket is null || key is null || status is null)
            {
                return false;
            }

            result = new GuardDutyScanResult(bucket, key, GetString(s3, "versionId"), status);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
