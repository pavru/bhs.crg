namespace BHS.CRG.Application.Activity;

/// <summary>Как слить «было» и «стало» серии в один ряд журнала (ТЗ CORE-28.3).</summary>
public enum FoldRule
{
    /// <summary>Первое непустое значение серии в «было», последнее непустое — в «стало».</summary>
    Span,

    /// <summary>Объединение перечней через «, » в порядке первого появления.</summary>
    Union,
}

/// <summary>
/// Объявление свёртки вида журнала (ТЗ CORE-28.3): какой группе правок он принадлежит и как сливать
/// значения серии.
///
/// <para>Группа, а не флаг: шапка и строки черновика при автосохранении чередуются, и сессия правки
/// держится, пока оба вида объявили одну группу (<see cref="ActivitySession" />). Без группы каждый
/// переход рвал бы сессию, без правила было бы неясно, что ряд показывает в «было» и «стало».</para>
///
/// <para>⚠️ Свёртка — свойство ЧТЕНИЯ. Записи серии остаются отдельными строками журнала и раскрываются
/// по одной; ряд их только показывает вместе.</para>
/// </summary>
public sealed record ActivityFold(string Group, FoldRule Before, FoldRule After)
{
    /// <summary>Разделитель перечня — тот же, которым издатели перечисляют поля.</summary>
    private const string Separator = ", ";

    /// <summary>
    /// «Было» и «стало» серии, записи которой идут по времени. Правило по умолчанию (вид без
    /// объявления, но с хвостом — свёртку сняли позже) — первое и последнее значение: ряд обязан
    /// показать хоть что-то, а не спрятать записи.
    /// </summary>
    public static (string? Before, string? After) Merge(
        ActivityFold? fold, IReadOnlyList<(string? Before, string? After)> series) =>
    (
        Merge(fold?.Before ?? FoldRule.Span, series.Select(s => s.Before).ToList(), first: true),
        Merge(fold?.After ?? FoldRule.Span, series.Select(s => s.After).ToList(), first: false)
    );

    private static string? Merge(FoldRule rule, IReadOnlyList<string?> values, bool first)
    {
        var present = values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        if (present.Count == 0) return null;

        return rule switch
        {
            FoldRule.Union => string.Join(Separator, present
                .SelectMany(v => v.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)),
            _ => first ? present[0] : present[^1],
        };
    }
}
