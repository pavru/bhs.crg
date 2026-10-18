namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Бумажная сумма строки и её ставка — то, по чему угадывается режим НДС.</summary>
/// <param name="Paper">Сумма строки, как она стоит в бумаге: у «в том числе» — <c>amount</c>, у «сверху» —
/// <c>netAmount</c>.</param>
/// <param name="Rate">Ставка НДС строки в процентах; <c>null</c> — не указана.</param>
public readonly record struct VatGuessLine(decimal Paper, decimal? Rate);

/// <summary>
/// Какой режим НДС видно по арифметике бумаги (эпик #1496, issue #1500; разбор Архитектора 18.10.2026,
/// §5.2).
///
/// <para><b>Одна функция — двое потребителей.</b> Готовность счёта (подсказка формы «похоже, цены без
/// НДС») и распознавание (#1501, режим строк скана). Одна — чтобы подсказка и решение распознавания не
/// расходились. На клиенте её не повторяют: подсказку отдаёт сервер пунктом готовности.</para>
///
/// <para>Ответ — про БУМАГУ, а не про счёт: по бумажным суммам строк, НДС шапки и сумме к оплате.
/// Поэтому та же функция ловит и обратный случай — флажок «сверху» поставлен зря: бумажные суммы такого
/// счёта уже сходятся с суммой к оплате.</para>
///
/// <para>⚠️ <b>Это догадка, не решение.</b> Ручной счёт она не переключает никогда, даже при точном
/// совпадении: расхождение бывает равно НДС случайно (скидка, доставка), а смена режима меняет деньги на
/// стройках. Режим меняет человек — флажком.</para>
/// </summary>
public static class VatModeGuess
{
    /// <param name="lines">Строки бумаги. Догадки нет, если строк нет: сравнивать нечего.</param>
    /// <param name="headerVat">НДС шапки («В том числе НДС»); <c>null</c> — не указан.</param>
    /// <param name="total">Сумма к оплате.</param>
    /// <param name="tolerance">Допуск расхождения — тот же, что у сверки суммы строк.</param>
    /// <returns><see cref="VatMode.Included" /> — бумажные суммы сходятся с суммой к оплате;
    /// <see cref="VatMode.OnTop" /> — сходятся, если прибавить НДС; <c>null</c> — ни то ни другое.</returns>
    public static VatMode? Of(IReadOnlyList<VatGuessLine> lines, decimal? headerVat, decimal total, decimal tolerance)
    {
        if (lines.Count == 0) return null;

        // «В том числе» — первым: при допуске больше НДС (счёт на копейки) сошлось бы и то и другое, а
        // сошедшийся счёт подсказки не заслуживает.
        var rest = total - lines.Sum(l => l.Paper);
        if (Math.Abs(rest) <= tolerance) return VatMode.Included;

        if (headerVat is > 0 && Math.Abs(rest - headerVat.Value) <= tolerance) return VatMode.OnTop;

        // НДС сверху по ставкам строк — тем же правилом, что досчёт строки (построчно, копейки «от нуля»).
        // Только когда ставка у КАЖДОЙ строки: сумма по части строк совпала бы с расхождением случайно.
        if (lines.All(l => l.Rate is not null))
        {
            var onTop = lines.Sum(l => InvoiceLineValues.Money(l.Paper * l.Rate!.Value / 100m));
            if (onTop > 0 && Math.Abs(rest - onTop) <= tolerance) return VatMode.OnTop;
        }

        return null;
    }
}
