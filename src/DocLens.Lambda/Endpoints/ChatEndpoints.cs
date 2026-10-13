using DocLens.Lambda.Models.Requests;
using DocLens.Lambda.Services.Chat;

namespace DocLens.Lambda.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Route is /procesos/{procesoId}/chat per the V1 domain (ADR-012's
        // explicit backend-orchestrated RAG flow) — procesoId isn't used
        // yet (see IChatService's note: proceso-level isolation needs the
        // procesos/permisos tables, out of this pass's scope), kept in the
        // route now so the contract doesn't need to change once they exist.
        app.MapPost("/procesos/{procesoId}/chat", Ask)
            .WithTags("Chat")
            .WithName("AskChat")
            .WithSummary("Retrieve + generate over a tenant's Knowledge Base, with PII placeholders substituted back for an authorized caller (ADR-001/012/014).");

        return app;
    }

    private static async Task<IResult> Ask(
        string procesoId,
        ChatRequest request,
        IChatService chatService,
        CancellationToken cancellationToken)
    {
        var response = await chatService.AskAsync(request.Pregunta, cancellationToken);
        return Results.Ok(response);
    }
}
