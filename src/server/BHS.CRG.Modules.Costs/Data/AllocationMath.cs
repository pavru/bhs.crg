namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Как разносится строка — решает сама строка, а не человек (ТЗ COST-10).</summary>
public enum AllocationMode
{
    /// <summary>Есть количество: человек делит метры и штуки, сумма части считается.</summary>
    Quantity,

    /// <summary>Количества нет (доставка, услуги) — делится сумма.</summary>
    Amount,

    /// <summary>Нет ни количества, ни суммы — делить нечего.</summary>
    None,
}

/// <summary>Строка — то, что арифметике нужно от неё знать.</summary>
public sealed record AllocationLine(Guid Id, int Ordinal, decimal? Quantity, decimal? Amount);

/// <summary>Часть разноски — то, что ввёл человек. <c>LineId = null</c> — часть счёта целиком (F2).</summary>
public sealed record AllocationPart(Guid Id, Guid? LineId, int Ordinal, decimal? Quantity, decimal? Amount);

/// <summary>Посчитанная часть.</summary>
/// <param name="Amount">Сумма части, со всеми поправками. <c>null</c> — посчитать нечем: у строки
/// количество есть, а суммы нет (цену не ввели), или часть не того вида, что строка.</param>
/// <param name="Rounding">Копейки округления, ушедшие в эту часть (ТЗ COST-13), — у последней части
/// разнесённой строки; у остальных нуль.</param>
/// <param name="Discrepancy">Расхождение суммы строк с суммой к оплате, ушедшее в эту часть, — в
/// пределах допуска и только у последней части счёта.</param>
/// <param name="Mismatched">Часть не того вида, что строка: у строки убрали количество, а часть
/// хранит метры (или наоборот). Такая часть ничего не разносит, и сказать об этом обязан экран.</param>
public sealed record AllocationShare(
    Guid Id, decimal? Amount, decimal Rounding, decimal Discrepancy, bool Mismatched);

/// <summary>Баланс строки (ТЗ COST-13).</summary>
/// <param name="UnallocatedQuantity">Сколько не разнесено количеством; <c>null</c> — строка
/// разносится не количеством.</param>
/// <param name="UnallocatedAmount">Сколько не разнесено деньгами; <c>null</c> — у строки нет суммы.
/// Отрицательное — разнесено больше, чем есть (строку уменьшили после разноски).</param>
public sealed record AllocationLineBalance(
    Guid LineId,
    int Ordinal,
    AllocationMode Mode,
    IReadOnlyList<AllocationShare> Parts,
    decimal? UnallocatedQuantity,
    decimal? UnallocatedAmount,
    bool Balanced);

/// <summary>
/// Разноска счёта целиком — суммой, пока строк нет (задача F2, issue #1086, ТЗ COST-11).
/// </summary>
/// <param name="UnallocatedAmount">Сумма к оплате минус разнесённое; <c>null</c> — считать не от чего:
/// у счёта есть строки (разносятся они) или нет суммы к оплате.</param>
/// <param name="Pending">Части счёта есть, а у счёта уже появились строки: разноска ждёт пересчёта по
/// строкам. Пока она ждёт, счёт не «разнесён» — деньги лежат на стройках суммой, а строки не разнесены
/// никуда, и сложи их вместе — счёт посчитался бы дважды.</param>
/// <param name="Balanced">Разноска счёта сходится: строк нет и разнесена вся сумма к оплате, либо
/// строки есть и частей счёта не осталось.</param>
public sealed record AllocationDocumentBalance(
    IReadOnlyList<AllocationShare> Parts,
    decimal? UnallocatedAmount,
    bool Pending,
    bool Balanced);

/// <summary>Баланс счёта целиком.</summary>
/// <param name="Discrepancy">Сумма к оплате минус сумма строк; <c>null</c> — суммы к оплате в счёте
/// нет или нет строк: сверять не с чем.</param>
public sealed record AllocationBalance(
    IReadOnlyList<AllocationLineBalance> Lines,
    AllocationDocumentBalance Document,
    decimal? Discrepancy,
    decimal Tolerance,
    bool WithinTolerance)
{
    /// <summary>Номера строк, у которых баланс не сходится, — их называет отказ «разобран».</summary>
    public IReadOnlyList<int> Unbalanced => [.. Lines.Where(l => !l.Balanced).Select(l => l.Ordinal)];

    /// <summary>
    /// «Разнесён» (ТЗ COST-9): каждая строка разнесена полностью, и расхождение с суммой к оплате — в
    /// пределах допуска.
    /// </summary>
    public bool Allocated => Lines.All(l => l.Balanced) && Document.Balanced && WithinTolerance;
}

/// <summary>
/// Арифметика разноски (задача F1, issue #1085, ТЗ COST-10, COST-13) — чистая, без базы.
///
/// <para>Одним местом, потому что зовут её трое: чтение счёта, переход «разобран» и правка строк и
/// частей. Посчитай каждый у себя — экран показывал бы «сходится», а переход отказывал бы из-за
/// копейки, которой на экране нет.</para>
/// </summary>
public static class AllocationMath
{
    /// <summary>
    /// Допуск расхождения суммы строк с суммой к оплате — 1 ₽ на счёт (ТЗ COST-13).
    ///
    /// <para>⚠️ По ТЗ это НАСТРОЙКА модуля, а настроек модуля ещё нет (M1, issue #1070). Число живёт
    /// здесь одним местом и уезжает в ответе счёта — форма его не повторяет, — так что с приездом
    /// настройки меняется только источник.</para>
    /// </summary>
    public const decimal Tolerance = 1.00m;

    /// <summary>Как разносится строка: есть количество — количеством, нет — суммой.</summary>
    public static AllocationMode ModeOf(decimal? quantity, decimal? amount) =>
        quantity is > 0 ? AllocationMode.Quantity
        : amount is { } value && value != 0 ? AllocationMode.Amount
        : AllocationMode.None;

    /// <summary>Баланс счёта: каждая строка отдельно, потом расхождение с суммой к оплате.</summary>
    /// <param name="total">Сумма к оплате из бумаги; <c>null</c> — её в счёте нет.</param>
    public static AllocationBalance Of(
        IEnumerable<AllocationLine> lines, IEnumerable<AllocationPart> parts, decimal? total,
        decimal tolerance = Tolerance)
    {
        var byLine = parts.ToLookup(p => p.LineId);
        var ordered = lines.OrderBy(l => l.Ordinal).ToList();
        var balances = ordered
            .Select(l => Line(l, [.. byLine[l.Id].OrderBy(p => p.Ordinal)]))
            .ToList();
        var document = Document(ordered.Count > 0, [.. byLine[null].OrderBy(p => p.Ordinal)], total);

        // Строк нет — сверять сумму к оплате не с чем: «расхождение» в размере всего счёта было бы не
        // расхождением, а отсутствием строк, о котором и так сказано.
        var discrepancy = total is { } paper && ordered.Count > 0
            ? paper - ordered.Sum(l => l.Amount ?? 0m)
            : (decimal?)null;
        var within = discrepancy is not { } d || Math.Abs(d) <= tolerance;

        // Расхождение с суммой к оплате уходит в ПОСЛЕДНЮЮ часть счёта (ТЗ COST-13) — и только когда
        // разнесено всё и расхождение в допуске. Разнесено не всё — оно остаётся числом у счёта, а не
        // прилипает к части, которую человек ещё будет править. Сверх допуска — не уходит никуда: это
        // повод разобраться, и счёт не станет «разобран».
        if (discrepancy is { } gap && gap != 0 && within && balances.All(b => b.Balanced))
            Absorb(balances, gap);

        return new AllocationBalance(balances, document, discrepancy, tolerance, within);
    }

    /// <summary>
    /// Разноска счёта целиком (F2, ТЗ COST-11). Без строк части счёта — вся разноска, и остаток считается
    /// от суммы к оплате. Со строками они — ожидание пересчёта: считать их разнесённым нельзя.
    /// </summary>
    public static AllocationDocumentBalance Document(bool hasLines, IReadOnlyList<AllocationPart> parts, decimal? total)
    {
        var shares = parts
            .Select(p => new AllocationShare(p.Id, p.Amount, 0m, 0m, Mismatched: p.Amount is null))
            .ToList();

        if (hasLines)
            return new AllocationDocumentBalance(shares, null, Pending: parts.Count > 0, Balanced: parts.Count == 0);

        var rest = total is { } whole ? whole - parts.Sum(p => p.Amount ?? 0m) : (decimal?)null;
        return new AllocationDocumentBalance(shares, rest, Pending: false,
            Balanced: parts.Count > 0 && rest == 0 && parts.All(p => p.Amount is not null));
    }

    /// <summary>
    /// Баланс строки.
    ///
    /// <para><b>Сумма части — доля суммы строки</b>, <c>сумма × количество части ÷ количество строки</c>,
    /// а не «количество × цена». Когда сумма строки равна количеству × цену, это одно и то же число. А
    /// когда бумага округлила сумму строки по-своему (или цены нет вовсе, а сумма есть), «количество ×
    /// цена» разошлось бы с суммой строки на её собственное расхождение, и всё оно легло бы в последнюю
    /// часть — нарушив «не больше копейки на часть» (ТЗ COST-13) без единой ошибки округления.</para>
    ///
    /// <para><b>Копейки — в последнюю часть, и только когда строка разнесена целиком.</b> Каждая часть
    /// округляется до копейки; последняя получает то, что осталось от суммы строки. Ошибка округления
    /// части — не больше полкопейки, значит поправка у последней — не больше полкопейки на часть. Пока
    /// строка разнесена не вся, копейки живут в «не разнесено», и части с остатком всё равно дают сумму
    /// строки.</para>
    ///
    /// <para>⚠️ Чем это ломается: округлить каждую часть и не поправить последнюю — тогда 100 ₽ на три
    /// части дают 33,33 × 3 = 99,99, и копейка пропадает из затрат молча.</para>
    /// </summary>
    public static AllocationLineBalance Line(AllocationLine line, IReadOnlyList<AllocationPart> parts) =>
        ModeOf(line.Quantity, line.Amount) switch
        {
            AllocationMode.Quantity => ByQuantity(line, parts, line.Quantity!.Value),
            AllocationMode.Amount => ByAmount(line, parts, line.Amount!.Value),

            // Делить нечего: у строки нет ни количества, ни суммы. Баланс сходится, пока частей нет;
            // часть на такой строке не разносит ничего, и выдавать её за «разнесено» нельзя.
            _ => new AllocationLineBalance(line.Id, line.Ordinal, AllocationMode.None,
                [.. parts.Select(p => new AllocationShare(p.Id, null, 0m, 0m, Mismatched: true))],
                null, null, Balanced: parts.Count == 0),
        };

    private static AllocationLineBalance ByQuantity(
        AllocationLine line, IReadOnlyList<AllocationPart> parts, decimal quantity)
    {
        var whole = line.Amount;
        var valid = parts.Where(p => p.Quantity is not null).ToList();
        var allocated = valid.Sum(p => p.Quantity!.Value);
        var last = allocated == quantity && valid.Count > 0 ? valid[^1].Id : (Guid?)null;

        var shares = new List<AllocationShare>(parts.Count);
        var spent = 0m;

        foreach (var part in parts)
        {
            if (part.Quantity is not { } share)
            {
                shares.Add(new AllocationShare(part.Id, null, 0m, 0m, Mismatched: true));
                continue;
            }

            if (whole is not { } amount)
            {
                shares.Add(new AllocationShare(part.Id, null, 0m, 0m, Mismatched: false));
                continue;
            }

            var exact = InvoiceLineValues.Money(amount * share / quantity);
            var value = part.Id == last ? amount - spent : exact;
            spent += value;
            shares.Add(new AllocationShare(part.Id, value, value - exact, 0m, Mismatched: false));
        }

        return new AllocationLineBalance(line.Id, line.Ordinal, AllocationMode.Quantity, shares,
            UnallocatedQuantity: quantity - allocated,
            UnallocatedAmount: whole is { } total ? total - spent : null,
            Balanced: valid.Count == parts.Count && allocated == quantity);
    }

    private static AllocationLineBalance ByAmount(
        AllocationLine line, IReadOnlyList<AllocationPart> parts, decimal amount)
    {
        var spent = parts.Sum(p => p.Amount ?? 0m);

        return new AllocationLineBalance(line.Id, line.Ordinal, AllocationMode.Amount,
            [.. parts.Select(p => new AllocationShare(p.Id, p.Amount, 0m, 0m, Mismatched: p.Amount is null))],
            UnallocatedQuantity: null,
            UnallocatedAmount: amount - spent,
            Balanced: parts.All(p => p.Amount is not null) && spent == amount);
    }

    /// <summary>
    /// Отдать расхождение с суммой к оплате последней части счёта, у которой есть сумма. Последняя —
    /// по порядку строк, потом по порядку частей: так она одна и та же на каждом чтении.
    /// </summary>
    private static void Absorb(List<AllocationLineBalance> balances, decimal gap)
    {
        for (var index = balances.Count - 1; index >= 0; index--)
        {
            var line = balances[index];
            var target = line.Parts.LastOrDefault(p => p.Amount is not null);
            if (target is null) continue;

            balances[index] = line with
            {
                Parts = [.. line.Parts.Select(p => p.Id == target.Id
                    ? p with { Amount = p.Amount + gap, Discrepancy = gap }
                    : p)],
            };
            return;
        }

        // Частей с суммой нет вовсе — расхождению некуда уйти, и оно остаётся числом у счёта.
    }
}
