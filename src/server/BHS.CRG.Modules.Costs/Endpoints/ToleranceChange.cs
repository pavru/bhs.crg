using System.Globalization;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Обработчик смены допуска расхождения (E2, issue #1100, spec v3.3 п. 1.9): предупреждение двумя числами,
/// книга запертым счетам и выравнивание после смены и при старте. Считает и пишет связка записи
/// (<c>InvoiceDesk.Tolerance.cs</c>); здесь — значения настройки, слова и журнал.
///
/// <para>Журнал — после записи модуля, как у любой правки счёта: ядро держит открытой свою транзакцию, и
/// события счетов ложатся в неё вместе со сменой настройки. Сорвётся ядро — пропадёт и след в журнале, а
/// книга останется (в ней автор и причина): меньшая из двух бед.</para>
/// </summary>
internal sealed class ToleranceChange(InvoiceDesk desk, IModuleActivityLog journal, ILogger<ToleranceChange> log)
    : IModuleSettingChange
{
    private static NumberSetting Tolerance => CostsSettings.AllocationTolerance;

    public ModuleSetting Setting => Tolerance;

    public async Task<ModuleSettingImpact> PreviewAsync(string? before, string? after, CancellationToken ct = default)
    {
        var impact = await desk.ToleranceImpactAsync(Tolerance.Read(before), Tolerance.Read(after), ct);
        return new(impact.Changed, impact.Booked, Text(impact));
    }

    public async Task ApplyAsync(ModuleSettingChange change, CancellationToken ct = default)
    {
        var (was, now) = (Tolerance.Read(change.Before), Tolerance.Read(change.After));
        var moved = await desk.RetoleranceAsync(change, was, now, Reason(was, now), ct);
        await RecordAsync(moved, now, ct);
    }

    public async Task AlignAsync(string? current, CancellationToken ct = default)
    {
        var now = Tolerance.Read(current);
        var moved = await desk.AlignToleranceAsync(now, was => Reason(was, now),
            (invoice, e) => log.LogError(e,
                "Книгу счёта {Invoice} не удалось выровнять к допуску {Tolerance}: следующая его запись откажет", invoice, now),
            ct);
        await RecordAsync(moved, now, ct);
    }

    private async Task RecordAsync(IReadOnlyList<ToleranceMoved> moved, decimal now, CancellationToken ct)
    {
        foreach (var invoice in moved)
            await journal.RecordAsync(InvoiceActions.ToleranceAligned, invoice.Id.ToString(), invoice.Label,
                Display(invoice.From), Display(now), ct);
    }

    /// <summary>Причина записи книги — «допуск 1,00 → 0,50»: она лежит в книге рядом с автором.</summary>
    private static string Reason(decimal was, decimal now) => $"допуск {Display(was)} → {Display(now)}";

    private static string Display(decimal value) => Tolerance.Display(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Предупреждение словами: экран ядра склонять «счёт» не умеет, а числа без слов человеку ничего не
    /// скажут о главном — сдвинутся ли закрытые месяцы.
    /// </summary>
    private static string Text(ToleranceImpact impact)
    {
        if (impact.Changed == 0) return "Суммы уже оплаченных счетов эта смена не меняет.";

        var text = $"Изменятся суммы {Count(impact.Changed, "оплаченного счёта", "оплаченных счетов", "оплаченных счетов")} " +
                   "по стройкам: расхождение перейдёт между последней частью разноски и остатком.";
        return impact.Booked == 0
            ? text + " Закрытых периодов и исправленных счетов среди них нет."
            : text + $" {Count(impact.Booked, "счёт заперт", "счёта заперты", "счетов заперты")} закрытым периодом или уже " +
              "исправлялись: смена запишет им книгу исправлений — закрытые месяцы останутся как были, а разница " +
              "ляжет днём смены.";
    }

    private static string Count(int n, string one, string few, string many) =>
        $"{n} " + ((n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many });
}
