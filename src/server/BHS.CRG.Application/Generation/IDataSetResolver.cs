namespace BHS.CRG.Application.Generation;

public interface IDataSetResolver
{
    /// <summary>
    /// Подмешивает данные наборов в контекст. Если передан <paramref name="diagnostics"/>,
    /// в него записываются проблемы маппинга (например, значение колонки не найдено в каталоге).
    /// </summary>
    Task InjectAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default);
}
