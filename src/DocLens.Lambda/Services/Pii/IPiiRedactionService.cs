namespace DocLens.Lambda.Services.Pii;

public record PiiRedactionResult(string RedactedText, int EntitiesFound);

public interface IPiiRedactionService
{
    // Detects PII in rawText, replaces each occurrence with a stable
    // placeholder ([PARTE_A], [FECHA_1]...), and persists the reversible
    // mapping to pii_mappings (ADR-014) — the caller never sees the
    // mapping itself, only the redacted text that's safe to write to
    // doclens-kb-source-{env}.
    Task<PiiRedactionResult> RedactAsync(
        string documentoId,
        string rawText,
        CancellationToken cancellationToken = default);
}
