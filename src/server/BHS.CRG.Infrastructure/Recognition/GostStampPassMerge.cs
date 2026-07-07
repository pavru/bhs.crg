namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Слияние результата двух проходов распознавания штампа: пасс-1 (вся страница) и пасс-2 (кроп
/// области штампа, отрендеренный в более высоком эффективном разрешении, см. GostTitleBlockRegion).
///
/// <para>
/// Пасс-2 всегда ПРИОРИТЕТЕН для полей, которые он реально прочитал — выше эффективное разрешение,
/// значит точнее (это исправляет случаи вроде «DP» ↔ «ДР» / «ЕЦДМ» ↔ «ЕЦ.ДМ», где пасс-1 на полной
/// странице ошибался, а кроп читал верно). При этом слияние — ОБЪЕДИНЕНИЕ, а не замена: поля,
/// которых во втором проходе нет (кроп физически не захватил графу; либо классификаторы
/// ТипСтраницы/Форма — их во втором проходе не запрашивают), берутся из пасс-1. Так пасс-2 никогда
/// не «теряет» данные, только уточняет.
/// </para>
/// </summary>
public static class GostStampPassMerge
{
    /// <summary>Возвращает объединение: значения пасс-2 (кроп) поверх пасс-1 (полная страница),
    /// но только там, где пасс-2 непуст; иначе остаётся значение пасс-1.</summary>
    public static Dictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string?> fullPage,
        IReadOnlyDictionary<string, string?> crop)
    {
        var merged = new Dictionary<string, string?>(fullPage);
        foreach (var (key, value) in crop)
            if (!string.IsNullOrWhiteSpace(value))
                merged[key] = value;
        return merged;
    }
}
