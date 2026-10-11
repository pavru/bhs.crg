using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Удаление накладной (issue #1283; решения владельца продукта 11.10.2026 — те же, что для счёта, #1282).
///
/// <para><b>Удаляется только черновик — насовсем.</b> Удаление — для накладной, заведённой по ошибке.
/// Проведённую сначала возвращают в черновик: её строки лежат в «Материалах на объекте», и отказ
/// называет именно этот выход.</para>
///
/// <para>Отдельной проверки «не заперта закрытым периодом» здесь нет, и это не пропуск: запирается
/// только проведённая накладная, а вернуть в черновик запертую связка записи не даёт (issue #1285).
/// Поэтому проведённая накладная закрытого месяца до удаления не доходит.</para>
///
/// <para>⚠️ <b>Удаление — отслеживаемое <c>Remove</c> под связкой записи</b> (<see cref="WaybillDesk" />),
/// не <c>ExecuteDelete</c>: двух писателей одной накладной разводит версия строки при сохранении, а
/// запись мимо отслеживания связка не видит вовсе. Строки уходят каскадом базы.</para>
///
/// <para>⚠️ <b>Журнал — после связки, без токена отмены и без права на отказ</b>, как у счёта:
/// накладная удалена насовсем, и оборванный запрос не вправе оставить это без записи, а отказ журнала —
/// ответить «сбой» про удаление, которое состоялось.</para>
/// </summary>
public static class WaybillDeletion
{
    /// <summary>Что осталось от накладной после удаления: чем назвать её в журнале.</summary>
    private sealed record Gone(string Label, string? Warehouse, Guid? ConstructionId, string? ReceivedBy, int Lines);

    /// <summary>
    /// Удалить накладную. Ответ — 204 без тела: читать после удаления нечего. Повтор отвечает 404 —
    /// клиент читает его как «уже удалена».
    /// </summary>
    public static async Task<NoContent> DeleteAsync(
        Guid id, CostsDbContext db, WaybillDesk desk, IModuleConstructions sites,
        IModuleActivityLog log, ILoggerFactory logs, CancellationToken ct)
    {
        Gone gone;
        try
        {
            gone = await desk.WriteAsync(id, async write =>
            {
                var waybill = write.Waybill;
                EnsureDeletable(waybill);

                var gone = new Gone(WaybillEndpoints.Label(waybill), waybill.Warehouse, waybill.ConstructionId,
                    waybill.ReceivedBy, await db.WaybillLines.CountAsync(l => l.WaybillId == waybill.Id, ct));
                db.Waybills.Remove(waybill);
                await db.SaveChangesAsync(ct);
                return gone;
            }, ct);
        }
        catch (ConflictException lost) when (lost.InnerException is DbUpdateConcurrencyException)
        {
            // Двое удаляли разом: второй прошёл сверку версии и не нашёл строки. Ему обязано ответить то
            // же «нет такой», что и повтору, — «накладную изменили, действие не выполнено» про удалённую
            // было бы неправдой. Накладная на месте — отказ остаётся отказом.
            db.ChangeTracker.Clear();
            if (await db.Waybills.AsNoTracking().AnyAsync(w => w.Id == id, CancellationToken.None)) throw;
            throw new NotFoundException("Накладная не найдена.");
        }

        try
        {
            await log.RecordAsync(WaybillActions.Deleted, id.ToString(), gone.Label,
                before: await DescribedAsync(sites, gone), after: "удалена насовсем", ct: CancellationToken.None);
        }
        catch (Exception lost)
        {
            logs.CreateLogger(typeof(WaybillDeletion)).LogError(lost,
                "{Label} ({WaybillId}) удалена, но запись об этом в журнал действий не легла.", gone.Label, id);
        }

        return TypedResults.NoContent();
    }

    private static void EnsureDeletable(Waybill waybill)
    {
        if (waybill.State != WaybillState.Draft)
            throw new ConflictException(
                $"{WaybillEndpoints.Label(waybill)} проведена, а удаляется только черновик: её строки лежат в " +
                "перечне материалов, отпущенных на стройку. Верните накладную в черновик — и удалите.");
    }

    /// <summary>
    /// Чем накладную опознать в журнале, когда её самой уже нет: склад, получатель и число строк. Номер и
    /// дата стоят в подписи записи. Денег у накладной нет, и скрывать здесь нечего.
    ///
    /// <para>Стройка читается ПОСЛЕ удаления — она запись ядра и от накладной не зависит; список строек,
    /// который не ответил, оставляет запись без имени стройки, а не без записи.</para>
    /// </summary>
    private static async Task<string> DescribedAsync(IModuleConstructions sites, Gone gone)
    {
        string? site = null;
        if (gone.ConstructionId is { } id)
        {
            try
            {
                site = (await sites.ListAsync(CancellationToken.None)).FirstOrDefault(s => s.Id == id)?.Name;
            }
            catch (Exception)
            {
                // Накладная удалена: отказ списка строек не вправе отнять у журнала саму запись об этом.
            }
        }

        var receiver = string.Join(", ", new[] { site, gone.ReceivedBy }.Where(part => part is { Length: > 0 }));
        return string.Join("; ", new[]
        {
            gone.Warehouse is { Length: > 0 } ? $"склад: {gone.Warehouse}" : null,
            receiver.Length > 0 ? $"получатель: {receiver}" : null,
            $"строк: {gone.Lines}",
        }.OfType<string>());
    }
}
