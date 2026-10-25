using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Подстановка сопоставлений со сверкой договора <see cref="IEstimateRowMatcher" />: строк столько же, сколько
/// пришло. Отдельным файлом нарочно — сторож исключений (<c>DomainExceptionPolicyTests</c>) пропускает его
/// целиком, а остальной код создания редакции отказывает человеку только доменными исключениями.
/// </summary>
internal static class EstimateRowMatching
{
    public static async Task<IReadOnlyList<EstimateRowDraft>> ApplyCheckedAsync(this IEstimateRowMatcher matcher,
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct)
    {
        var matched = await matcher.ApplyAsync(target, rows, ct);
        if (matched.Count != rows.Count)
            throw new InvalidOperationException($"Сопоставление вернуло {matched.Count} строк вместо {rows.Count}.");
        return matched;
    }
}
