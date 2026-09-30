namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Цель быстрой разноски и её вес: процент, поровну — единица, пересчёт — прежняя сумма.</summary>
public sealed record SplitTarget(Guid ConstructionId, Guid? SectionId, decimal Weight);

/// <summary>Предложенная часть: куда, сколько — и ушёл ли в неё остаток округления.</summary>
/// <param name="LineId">Строка; <c>null</c> — часть счёта целиком (счёт без строк).</param>
/// <param name="Remainder">В эту часть ушёл остаток округления (ТЗ COST-12): экран её помечает, иначе
/// «4» среди «3» выглядело бы опечаткой.</param>
public sealed record SplitPart(
    Guid? LineId, Guid ConstructionId, Guid? SectionId, decimal? Quantity, decimal? Amount, bool Remainder);

/// <summary>
/// Быстрая разноска счёта одной пропорцией (задача F2, issue #1086, ТЗ COST-12) — чистая, без базы.
///
/// <para><b>Раскладку считает СЕРВЕР, экран её только рисует.</b> Посчитай её форма — «предпросмотр
/// совпадает с применённым» сравнивал бы форму с формой и был бы зелен всегда, в том числе когда форма
/// и сервер округляют по-разному.</para>
///
/// <para><b>Округление — до единицы измерения строки</b> (ТЗ COST-12): у строки «300 м» части целые, у
/// «12,5 м» — с десятыми, у суммы — с копейками. Точность берётся из самой строки: сколько знаков у её
/// количества, столько и у частей. Иначе «10 шт на три объекта» дало бы 3,333 шт — число, которого нет
/// ни на складе, ни на объекте.</para>
///
/// <para><b>Остаток — в последнюю часть, и только в неё.</b> Каждая часть, кроме последней, округляется
/// К НУЛЮ, последняя получает то, что осталось. ⚠️ Чем это ломается: округлить к ближайшему — тогда
/// «2 шт на четыре объекта» даёт 1 + 1 + 1 и последней −1, то есть минус штуку на стройке. Округление к
/// нулю этого не допускает: последняя часть не меньше своей точной доли.</para>
///
/// <para>Часть, которой по округлению не досталось ничего, не заводится: «1 шт на три объекта» уходит
/// последней стройке целиком, и предпросмотр показывает это до применения.</para>
/// </summary>
public static class AllocationSplit
{
    /// <summary>Сколько знаков после запятой у количества строки — не больше трёх, как хранит колонка.</summary>
    public static int Digits(decimal quantity)
    {
        var trimmed = quantity / 1.000000000000000000000000000000000m;
        return Math.Min(3, (decimal.GetBits(trimmed)[3] >> 16) & 0xFF);
    }

    /// <summary>
    /// Разделить <paramref name="whole" /> по весам с точностью <paramref name="digits" /> знаков. Сумма
    /// долей — ровно <paramref name="whole" />; остаток округления — у последней.
    /// </summary>
    public static IReadOnlyList<decimal> Split(decimal whole, IReadOnlyList<decimal> weights, int digits)
    {
        if (weights.Count == 0) throw new ArgumentException("Делить не на что.", nameof(weights));
        if (weights.Any(w => w <= 0)) throw new ArgumentException("Вес доли — больше нуля.", nameof(weights));

        var sum = weights.Sum();
        var shares = new decimal[weights.Count];
        var spent = 0m;

        for (var index = 0; index < weights.Count - 1; index++)
        {
            shares[index] = decimal.Round(whole * weights[index] / sum, digits, MidpointRounding.ToZero);
            spent += shares[index];
        }

        shares[^1] = whole - spent;
        return shares;
    }

    /// <summary>
    /// Разложить счёт по целям: каждую строку — её количеством (или суммой, если количества нет); счёт
    /// без строк — суммой к оплате.
    /// </summary>
    /// <param name="total">Сумма к оплате — нужна только счёту без строк.</param>
    /// <param name="wholeUnits">Округлять количество до единицы строки (ТЗ COST-12). У ПЕРЕСЧЁТА разноски
    /// суммой — нет: там человек уже решил, сколько ДЕНЕГ идёт на объект, и целые метры сдвинули бы это
    /// решение на десятки рублей (40 м и 8 шт на три объекта по трети: 310 / 310 / 380 ₽ вместо
    /// 333 / 333 / 334). Пересчёт делит с точностью колонки — до тысячной.</param>
    public static IReadOnlyList<SplitPart> Plan(
        IReadOnlyList<AllocationLine> lines, decimal? total, IReadOnlyList<SplitTarget> targets,
        bool wholeUnits = true)
    {
        var weights = targets.Select(t => t.Weight).ToList();

        if (lines.Count == 0)
            return total is { } whole && whole != 0 ? [.. Parts(null, whole, 2, amount: true, targets, weights)] : [];

        return [.. lines.OrderBy(l => l.Ordinal).SelectMany(line => AllocationMath.ModeOf(line.Quantity, line.Amount) switch
        {
            AllocationMode.Quantity => Parts(line.Id, line.Quantity!.Value, wholeUnits ? Digits(line.Quantity.Value) : 3,
                amount: false, targets, weights),
            AllocationMode.Amount => Parts(line.Id, line.Amount!.Value, 2, amount: true, targets, weights),
            _ => [],
        })];
    }

    private static IEnumerable<SplitPart> Parts(
        Guid? lineId, decimal whole, int digits, bool amount, IReadOnlyList<SplitTarget> targets,
        IReadOnlyList<decimal> weights)
    {
        var shares = Split(whole, weights, digits);

        for (var index = 0; index < targets.Count; index++)
        {
            if (shares[index] == 0) continue;

            var target = targets[index];
            // Остаток есть, когда последняя получила больше, чем дало бы ей то же округление, что у всех.
            var own = decimal.Round(whole * weights[index] / weights.Sum(), digits, MidpointRounding.ToZero);
            yield return new SplitPart(lineId, target.ConstructionId, target.SectionId,
                amount ? null : shares[index], amount ? shares[index] : null,
                Remainder: index == targets.Count - 1 && shares[index] != own);
        }
    }
}
