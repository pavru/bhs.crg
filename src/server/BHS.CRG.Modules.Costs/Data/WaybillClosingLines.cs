using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Накладные в диалоге закрытия периода (issue #1285; ТЗ CORE-35, COST-16).
///
/// <para><b>«Запрутся» считает то же правило, что запирает</b> (<see cref="WaybillLock" />): проведённая
/// накладная, по учётной дате, а без неё — по дате отпуска. Посчитай диалог по дате отпуска — он назвал
/// бы накладную, введённую после прошлого закрытия и лежащую в открытом дне, и промолчал бы о той,
/// что запрётся на самом деле.</para>
///
/// <para>Денег у строк нет: накладная их не несёт. Ссылок тоже: экран накладных отбора по периоду не
/// знает, а ссылка, по которой число не сходится, хуже её отсутствия.</para>
/// </summary>
public static class WaybillClosingLines
{
    private static readonly ModuleClosingUnit Waybills = new("накладная", "накладные", "накладных");

    public static async Task<(IReadOnlyList<ModuleClosingLine> Unfinished, IReadOnlyList<ModuleClosingLine> Frozen)> OfAsync(
        CostsDbContext db, ModuleClosingScope scope, CancellationToken ct)
    {
        var first = scope.From ?? DateOnly.MinValue;
        var site = scope.ConstructionId;

        // День накладной: у проведённой — учётная дата (нет её — дата отпуска, как у замка), у черновика —
        // дата отпуска: другой у него нет. Закрытие стройки касается только её накладных; закрытие
        // компании — всех, и черновика без стройки тоже.
        var read = await db.Waybills.AsNoTracking()
            .Where(w => site == null || w.ConstructionId == site)
            .Select(w => new
            {
                w.State, w.ConstructionId,
                Day = w.State == WaybillState.Posted ? w.AccountingOn ?? w.IssuedOn : w.IssuedOn,
                Unmatched = db.WaybillLines.Any(l => l.WaybillId == w.Id && l.NomenclatureId == null),
            })
            .Where(w => w.Day >= first && w.Day <= scope.Through)
            .ToListAsync(ct);
        // Стройка, закрытая своим закрытием дальше компании, уже заперла свои накладные этих дней: второй
        // раз их не называем — как деньги счетов.
        var mine = read.Where(w => scope.ClosesAnew(w.ConstructionId, w.Day!.Value)).ToList();

        var posted = mine.Where(w => w.State == WaybillState.Posted).ToList();
        var drafts = mine.Count(w => w.State != WaybillState.Posted);
        var unmatched = posted.Count(w => w.Unmatched);

        List<ModuleClosingLine> unfinished = [], frozen = [];
        if (unmatched > 0)
            unfinished.Add(new("waybills-unmatched", "Проведены в периоде, но есть строки без позиции номенклатуры",
                unmatched, Waybills,
                Note: "Такие строки в «Материалы на объекте» не попадают. После закрытия сопоставить их будет нельзя."));
        if (drafts > 0)
            unfinished.Add(new("waybills-drafts", "Черновики накладных с датой отпуска в периоде", drafts, Waybills,
                Note: "Закрытие их не запирает. Проведённый после закрытия черновик ляжет в первый открытый день, " +
                      "а не в этот период."));
        if (posted.Count > 0)
            frozen.Add(new("waybills", "Проведённые накладные, вошедшие в период", posted.Count, Waybills,
                Note: "Их нельзя будет вернуть в черновик, править и сопоставлять."));

        return (unfinished, frozen);
    }
}
