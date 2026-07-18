using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IDocumentGenerator
{
    Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default);
}

public record GenerationRequest(
    string TemplateContent,
    OutputFormat Format,
    GenerationContext Context,
    string? TypeBlocksContent = null,
    string? UserLibContent = null,
    ResolvedTemplateAssets? TemplateAssets = null
);
