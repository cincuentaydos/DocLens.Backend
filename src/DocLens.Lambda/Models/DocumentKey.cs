using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DocLens.Lambda.Models;

// S3 key convention for uploaded document versions (ADR-005):
//   {tenantId}/documents/{documentId}/v{versionNumber}.{extension}
// DocumentUploadService builds keys with Build(); every consumer (the
// /documents/process guard, the Processor Lambda) parses them with TryParse()
// so the convention lives in exactly one place.
public sealed partial record DocumentKey(string TenantId, Guid DocumentId, int VersionNumber, string Extension)
{
    public const string DocumentsSegment = "documents";

    public string Value => Build(TenantId, DocumentId, VersionNumber, Extension);

    public static string Build(string tenantId, Guid documentId, int versionNumber, string extension) =>
        $"{tenantId}/{DocumentsSegment}/{documentId}/v{versionNumber}.{extension}";

    // Prefix every object of one document shares, across all its versions.
    public static string DocumentPrefix(string tenantId, Guid documentId) =>
        $"{tenantId}/{DocumentsSegment}/{documentId}/";

    public static bool TryParse(string? s3Key, [NotNullWhen(true)] out DocumentKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(s3Key))
        {
            return false;
        }

        var parts = s3Key.Split('/');
        if (parts.Length != 4
            || string.IsNullOrWhiteSpace(parts[0])
            || parts[1] != DocumentsSegment
            || !Guid.TryParse(parts[2], out var documentId))
        {
            return false;
        }

        var match = FileNamePattern().Match(parts[3]);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var version) || version < 1)
        {
            return false;
        }

        key = new DocumentKey(parts[0], documentId, version, match.Groups[2].Value);
        // Reject anything that doesn't round-trip exactly (e.g. "v01", upper-case GUIDs).
        if (key.Value != s3Key)
        {
            key = null;
            return false;
        }

        return true;
    }

    [GeneratedRegex("^v([0-9]+)\\.([a-z0-9]+)$")]
    private static partial Regex FileNamePattern();
}
