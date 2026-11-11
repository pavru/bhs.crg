using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IDocumentGenerator
{
    Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Отладочный пакет (ZIP): каталог компиляции, записанный ТЕМ ЖЕ кодом, что у генерации, и README —
    /// чем компилировать и почему генерация этого документа откажет (change
    /// core-generation-input-one-path, D3, D5). Каталог и его архив — забота генератора: вызывающий не
    /// знает ни раскладки, ни материализаторов.
    /// </summary>
    Task<byte[]> DebugBundleAsync(GenerationRequest request, BundleNotes notes, CancellationToken ct = default);
}

/// <summary>
/// Что пакет говорит о генерации сверх каталога: ошибки проверок, на которых она откажет. Картинки,
/// которых хранилище не отдало, добавляет сам генератор — их знает только запись каталога.
/// </summary>
public sealed record BundleNotes(IReadOnlyList<ResolutionDiagnostic> Refusals)
{
    public static readonly BundleNotes None = new([]);
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
