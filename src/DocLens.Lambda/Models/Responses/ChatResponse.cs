namespace DocLens.Lambda.Models.Responses;

public record ChatResponse
{
    public required string Respuesta { get; init; }
    public required IReadOnlyList<string> Fuentes { get; init; }
}
