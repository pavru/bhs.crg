using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IDocumentGenerator
{
    Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default);
}

public record GenerationRequest(
    DocumentInstance Instance,
    string TemplateContent,
    OutputFormat Format,
    GenerationContext Context,
    string PageSize = "A4",
    string PageOrientation = "portrait",
    int MarginTop = 20,
    int MarginRight = 15,
    int MarginBottom = 20,
    int MarginLeft = 30,
    string? TypeBlocksContent = null,
    string? UserLibContent = null,
    IReadOnlyDictionary<string, ImageRenderOptions>? ImageOptions = null
);
