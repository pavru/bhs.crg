using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Накладные в диалоге закрытия периода (issue #1285; ТЗ CORE-35, COST-16).
///
/// <para><b>«Запрутся» считает то же правило, что запирает</b> — буквально: день проведённой накладной
/// называет <see cref="WaybillLock.DayOf" />, один на замок и на диалог. Посчитай диалог по дате
/// отпуска — он назвал бы накладную, введённую после прошлого закрытия и лежащую в открытом дне, и
/// промолчал бы о той, что запрётся на самом деле.</para>
///
/// <para>Денег у строк нет: накладная их не несёт. Ссылок тоже: экран накладных отбора по периоду не
/// знает, а ссылка, по которой число не сходится, хуже её отсутствия.</para>
/// </summary>
public static class WaybillClosingLines
{
    private static readonly ModuleClosingUnit Waybills = new("накладная", "накладные", "накладных");

    // Своя единица, а не «накладная»: футер диалога и журнал называют незавершённое числами подряд, и
    // «2 накладные, 1 накладная» читалось бы как опечатка (ревью PR #1297).
    private static readonly ModuleClosingUnit Drafts = new("черновик накладной", "черновика накладных", "черновиков накладных");

    public static async Task<(IReadOnlyList<ModuleClosingLine> Unfinished, IReadOnlyList<ModuleClosingLine> Frozen)> OfAsync(
        CostsDbContext db, ModuleClosingScope scope, CancellationToken ct)
    {
        var site = scope.ConstructionId;
        var (first, through) = (scope.From ?? DateOnly.MinValue, scope.Through);

        // Запрос отбирает С ЗАПАСОМ, решает правило в памяти: день накладной называет WaybillLock, и
        // переписывать его здесь выражением для базы значило бы держать вторую копию (ревью PR #1297).
        // Проведённая — хоть одной датой в отрезке; черновик — датой отпуска не позже конца: нижней
        // границы у него нет нарочно, см. DraftDay. Закрытие стройки касается только её накладных;
        // закрытие компании — всех, и черновика без стройки тоже.
        var read = await db.Waybills.AsNoTracking()
            .Where(w => site == null || w.ConstructionId == site)
            .Where(w => w.State == WaybillState.Posted
                ? (w.AccountingOn >= first && w.AccountingOn <= through) || (w.IssuedOn >= first && w.IssuedOn <= through)
                : w.IssuedOn <= through)
            .Select(w => new
            {
                w.State, w.ConstructionId, w.AccountingOn, w.IssuedOn,
                // Строки без позиции спрашиваем только у проведённых: у черновика они ничего не значат.
                Unmatched = w.State == WaybillState.Posted
                    && db.WaybillLines.Any(l => l.WaybillId == w.Id && l.NomenclatureId == null),
            })
            .ToListAsync(ct);

        // Стройка, закрытая своим закрытием дальше компании, уже заперла свои накладные этих дней: второй
        // раз их не называем — как деньги счетов.
        var posted = read
            .Where(w => WaybillLock.DayOf(w.State, w.AccountingOn, w.IssuedOn) is { } day && scope.ClosesAnew(w.ConstructionId, day))
            .ToList();
        var drafts = read.Count(w => w.State != WaybillState.Posted
            && DraftDay(w.IssuedOn!.Value, w.ConstructionId, scope) <= through);
        var unmatched = posted.Count(w => w.Unmatched);

        List<ModuleClosingLine> unfinished = [], frozen = [];
        if (unmatched > 0)
            unfinished.Add(new("waybills-unmatched", "Проведены в периоде, но есть строки без позиции номенклатуры",
                unmatched, Waybills,
                Note: "Такие строки в «Материалы на объекте» не попадают. После закрытия сопоставить их можно будет " +
                      "только исправлением с причиной."));
        if (drafts > 0)
            unfinished.Add(new("waybills-drafts", "Не проведены, а проведённые сейчас вошли бы в период", drafts, Drafts,
                Note: "Закрытие их не запирает. Проведённый после закрытия черновик ляжет в первый открытый день, " +
                      "а не в этот период."));
        if (posted.Count > 0)
            frozen.Add(new("waybills", "Проведённые накладные, вошедшие в период", posted.Count, Waybills,
                Note: "Их нельзя будет вернуть в черновик, а править и сопоставлять — только исправлением с причиной."));

        return (unfinished, frozen);
    }

    /// <summary>
    /// День, в который черновик лёг бы, проведи его сейчас: первый открытый день не раньше даты отпуска
    /// (то, что при проведении называет <see cref="WaybillLock.AccountedOn" />). Поэтому черновик с датой
    /// отпуска в УЖЕ закрытых днях тоже относится к закрываемому периоду: сегодня он лёг бы в его первый
    /// день, а после закрытия уедет в следующий — и молчать о нём нельзя (ревью PR #1297).
    ///
    /// <para>Границы — из вопроса ядра, а не из базы: начало отрезка и есть первый открытый день
    /// контура, а стройку, закрытую дальше компании, ядро называет в <c>ClosedAhead</c>.</para>
    /// </summary>
    private static DateOnly DraftDay(DateOnly issuedOn, Guid? site, ModuleClosingScope scope)
    {
        var day = scope.From is { } from && issuedOn < from ? from : issuedOn;
        return site is { } own && scope.ClosedAhead.TryGetValue(own, out var closed) && day <= closed
            ? closed.AddDays(1) : day;
    }
}
