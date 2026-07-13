using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Generation;

public interface IDataSetResolver
{
    /// <summary>
    /// Подмешивает данные наборов в контекст. Если передан <paramref name="diagnostics"/>,
    /// в него записываются проблемы маппинга (например, значение колонки не найдено в каталоге).
    /// </summary>
    Task InjectAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default);

    /// <summary>
    /// Резолвит привязки объекта для ПЕРСИСТА (issue #99): @@ref → {$ref:catalog, entryId} (нет матча —
    /// пропуск + WARNING в <paramref name="diagnostics"/>). Scope берётся из расположения объекта.
    /// Используется sync-on-save общих данных вместо display-превью — чтобы составное поле хранило
    /// настоящую ссылку, а не строку «🔗 …».
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> ResolveOwnerBindingsAsync(
        Guid ownerId, Guid typeId, CatalogScope scopeLevel, Guid? scopeId,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default);
}
