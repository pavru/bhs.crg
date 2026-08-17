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
    // Блоки типов (issue #772) — агрегатор typeblocks.typ + модули typeblocks/<слаг>.typ.
    // Раскладываются в tmpDir по своим относительным путям; агрегатор обязан быть даже пустым.
    IReadOnlyList<TypstBlockFile>? TypeBlocksFiles = null,
    string? UserLibContent = null,
    ResolvedTemplateAssets? TemplateAssets = null,
    // Дерево библиотеки (issue #473) — материализуется в подпапку userlib/ рядом с точкой входа.
    IReadOnlyList<UserLibFile>? UserLibFiles = null
);
