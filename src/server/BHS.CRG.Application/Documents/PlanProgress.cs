namespace BHS.CRG.Application.Documents;

/// <summary>
/// Готовность по плану на одном уровне (issue #796).
///
/// <paramref name="Planned"/> — сумма планируемых количеств; <paramref name="Ready"/> — сколько из
/// них закрыто (см. <see cref="PlanMath"/>); <paramref name="NeedsAttention"/> — неразобранное
/// сверкой на этом уровне; <paramref name="SetsWithoutPlan"/> — сколько комплектов под уровнем
/// плана не имеют и потому в проценте не участвуют.
/// </summary>
public record PlanProgress(int Planned, int Ready, int NeedsAttention, int SetsWithoutPlan)
{
    /// <summary>План есть? Ноль планируемых — это «плана нет», а не «ничего не готово».</summary>
    public bool HasPlan => Planned > 0;

    public int? Percent => PlanMath.Percent(Planned, Ready, NeedsAttention);
}

/// <summary>Готовность ребёнка уровня — для маркеров на пунктах, ведущих вниз.</summary>
public record PlanProgressOf(Guid Id, PlanProgress Progress);

/// <summary>Свой уровень + разбивка по непосредственным детям, одним ответом (как ProblemSummary, #454).</summary>
public record PlanSummary(PlanProgress Own, IReadOnlyList<PlanProgressOf> Children);

/// <summary>
/// Арифметика готовности. Отдельно от запросов — потому что вся спорная часть фичи именно здесь, и
/// проверяться она должна без базы.
/// </summary>
public static class PlanMath
{
    /// <summary>
    /// Закрыто планом: <c>Σ min(готовых типа, план типа)</c>.
    ///
    /// <c>min</c> обязателен: пять актов при плане в три — это три закрытые позиции и два документа
    /// сверх плана, а не 167 % готовности. Типы, которых в плане нет, не участвуют вовсе — иначе
    /// внеплановая работа надувала бы процент по чужим строкам.
    /// </summary>
    public static int Ready(IEnumerable<(int Planned, int Actual)> byType)
        => byType.Sum(x => Math.Min(x.Actual, x.Planned));

    /// <summary>
    /// Процент готовности, или null — если плана нет.
    ///
    /// Сверка здесь ГЕЙТ, а не множитель: 100 % показывается только когда план закрыт И разбирать
    /// нечего; иначе процент упирается в 99 %, а неразобранное показывается рядом отдельным
    /// маркером. Мешать в одной цифре две разные природы — количество документов и качество
    /// данных — значит получить число, которое ни о чём не говорит.
    ///
    /// Округление ВНИЗ по той же причине: 99,6 % это ещё не «сто», и показать «100 %» там, где
    /// одна позиция не закрыта, — прямая ложь на самом заметном месте экрана.
    /// </summary>
    public static int? Percent(int planned, int ready, int needsAttention)
    {
        if (planned <= 0) return null;

        var closed = Math.Clamp(ready, 0, planned);
        if (closed == planned) return needsAttention == 0 ? 100 : 99;

        return Math.Min(99, (int)Math.Floor(closed * 100.0 / planned));
    }
}
