using DocLens.Lambda.Models.Responses;

namespace DocLens.Lambda.Services.Chat;

public interface IChatService
{
    // NOTE: enforces tenant isolation (empresa_id) only. ADR-001's
    // "regla crítica" also requires proceso_id-level isolation, but the
    // procesos/permisos tables don't exist in Aurora yet — that's a
    // separate, larger piece of the V1 domain model outside this
    // implementation pass's scope (the document processing pipeline).
    // Do not treat this as ADR-001-complete.
    Task<ChatResponse> AskAsync(string pregunta, CancellationToken cancellationToken = default);
}
