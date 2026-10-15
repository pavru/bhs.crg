using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Пробел готовности, опознанный парой: строка (нет — счёт целиком) и вид пробела.</summary>
internal readonly record struct CorrectionGap(Guid? Line, string Kind);

/// <summary>
/// «Не хуже, чем было» (E2, issue #1100, spec v3.3 п. 1.4) — чем исправление проверяет свой итог.
///
/// <para><b>Почему не «счёт обязан быть сведён»</b>, как у правки оплаченного счёта. Исправляют счёт,
/// который уже лежит в закрытом периоде, и лежит он там не всегда безупречным: допуск с тех пор понизили,
/// позицию номенклатуры удалили в ядре. Потребуй исправление безупречности — опечатку в наименовании
/// нельзя было бы исправить, пока не починено всё остальное, а починить остальное, кроме как
/// исправлением, нечем. Поэтому исправлению можно оставить то, что было, но нельзя добавить нового.</para>
///
/// <list type="bullet">
/// <item><b>Сумма:</b> <c>|расхождение после| ≤ max(допуск, |расхождение до|)</c>.</item>
/// <item><b>Пробелы</b> (потерянная ссылка, неразнесённая строка, строка без цены) — парой (строка, вид):
/// отказ только на пару, которой до исправления не было. Строку узнаём по идентификатору: номер строки
/// исправление вправе сдвинуть.</item>
/// </list>
/// </summary>
internal static class InvoiceCorrectionCheck
{
    /// <summary>Пробелы готовности счёта — по снимку, из которого собирается и ответ чтения.</summary>
    public static IReadOnlySet<CorrectionGap> Gaps(Invoice invoice, InvoiceSnapshot seen)
    {
        var gaps = new HashSet<CorrectionGap>();

        foreach (var (field, id) in new[]
                 {
                     (InvoiceRequisites.SupplierKey, invoice.SupplierId),
                     (InvoiceRequisites.PayerKey, invoice.PayerId),
                 })
            if (id is { } organization && seen.Lost.Contains(organization))
                gaps.Add(new CorrectionGap(null, "reference:" + field));

        foreach (var line in seen.Lines)
        {
            // Справочника нет вовсе — не потеря позиции, а состояние установки (как у готовности).
            if (seen.Names is not null && line.NomenclatureId is { } position && !seen.Names.ContainsKey(position))
                gaps.Add(new CorrectionGap(line.Id, "position"));
            if (line.Amount is null) gaps.Add(new CorrectionGap(line.Id, "price"));
        }

        var byOrdinal = seen.Lines.ToDictionary(l => l.Ordinal, l => l.Id);
        foreach (var ordinal in seen.Allocation.Summary.Unbalanced)
            if (byOrdinal.TryGetValue(ordinal, out var id)) gaps.Add(new CorrectionGap(id, "allocation"));

        if (seen.Allocation.Summary.Document.Pending) gaps.Add(new CorrectionGap(null, "pending"));

        return gaps;
    }

    /// <summary>Отказ, если исправление сделало счёт хуже, чем он был.</summary>
    /// <param name="was">Снимок счёта до исправления — под теми же замками.</param>
    /// <param name="wasGaps">Его пробелы (<see cref="Gaps" />), посчитанные до правки.</param>
    /// <param name="seen">Снимок после правки — до учётных дат и книги.</param>
    public static void EnsureNoWorse(
        Invoice invoice, InvoiceSnapshot was, IReadOnlySet<CorrectionGap> wasGaps, InvoiceSnapshot seen)
    {
        var label = InvoiceEndpoints.Label(invoice);
        if (invoice.Total is not { } total || total == 0)
            throw new InvalidRequestException(
                $"{label}: у исправленного счёта не указана сумма к оплате. Счёт оплачен, и его сумма уже вошла " +
                "в затраты: без неё не из чего считать ни остаток, ни учётные дни.");

        var problems = new List<string>();

        // Предел — допуск, а если счёт и до исправления расходился сверх него, — прежнее расхождение.
        var before = was.Allocation.Summary;
        var after = seen.Allocation.Summary;
        var limit = before.WithinTolerance || before.Discrepancy is not { } old
            ? seen.Tolerance
            : Math.Max(seen.Tolerance, Math.Abs(old));
        if (!after.WithinTolerance && after.Discrepancy is { } gap && Math.Abs(gap) > limit)
            problems.Add($"сумма строк расходится с суммой к оплате {total:0.00} на {Math.Abs(gap):0.00}, а до " +
                $"исправления — не больше чем на {limit:0.00}");

        var ordinals = seen.Lines.ToDictionary(l => l.Id, l => l.Ordinal);
        var fresh = Gaps(invoice, seen).Except(wasGaps)
            .OrderBy(g => g.Line is { } id ? ordinals.GetValueOrDefault(id) : 0).ThenBy(g => g.Kind)
            .Select(g => Words(g, g.Line is { } id ? ordinals.GetValueOrDefault(id) : 0));
        problems.AddRange(fresh);

        if (problems.Count > 0)
            throw new InvalidRequestException(
                $"{label}: исправление сделало бы счёт хуже, чем он был, — {string.Join("; ", problems)}. " +
                "Исправление вправе оставить прежнее, но не добавить нового: счёт уже лежит в закрытом периоде, " +
                "и сломанное здесь чинить было бы нечем, кроме следующего исправления.");
    }

    private static string Words(CorrectionGap gap, int ordinal) => gap.Kind switch
    {
        "position" => $"позиции номенклатуры нет в справочнике у строки {ordinal}",
        "price" => $"у строки {ordinal} не посчитать сумму — не вписана цена",
        "allocation" => $"строка {ordinal} разнесена по стройкам не полностью",
        "pending" => "разноска счёта суммой ждёт пересчёта по строкам",
        var kind when kind.StartsWith("reference:", StringComparison.Ordinal) =>
            $"организации из поля «{InvoiceRequisites.TitleOf(kind["reference:".Length..])}» нет в справочнике",
        var kind => kind,
    };
}
