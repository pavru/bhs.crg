using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Запись запомненного — одним правилом для шифров (<see cref="RateMatching" />) и ресурсов
/// (<see cref="ResourceMatching" />), по образцу <c>SupplierMatching.RememberAsync</c> модуля <c>costs</c>.
///
/// <para>⚠️ <b>Вне транзакции сопоставления, и нарочно</b>: таблица запомненного общая, и две сметы,
/// сопоставляемые разом, добавляют один и тот же ключ — второй получает отказ единственного индекса. Внутри
/// транзакции сопоставления этот отказ погубил бы само сопоставление; здесь он значит «кто-то успел раньше» —
/// перечитываем и повторяем один раз.</para>
/// </summary>
internal static class Remembering<TMatch> where TMatch : class
{
    /// <summary>
    /// Записать запомненное. <paramref name="stage" /> читает знакомые ключи и добавляет либо перенацеливает
    /// записи в отслеживании, отвечая «сколько записано, сколько из них заменили прежнее»; при гонке его зовут
    /// второй раз — на свежем чтении.
    /// </summary>
    public static async Task<MatchMemory> SaveAsync(
        PlanDbContext db, Func<CancellationToken, Task<(int Remembered, int Replaced)>> stage, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (remembered, replaced) = await stage(ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return new(remembered, replaced);
            }
            catch (Exception lost) when (attempt == 0
                && lost is DbUpdateException or ConflictException { InnerException: DbUpdateConcurrencyException })
            {
                // Кто-то запомнил тот же ключ раньше или перенацелил его из списка, пока мы читали. Свои
                // несохранённые записи — из-под отслеживания, иначе повтор записал бы их снова.
                Detach(db);
            }
        }
    }

    /// <summary>
    /// Запомнить, не дав отказу запоминания стать отказом сопоставления: сопоставление к этому мгновению
    /// записано. Пробрось мы отказ — человек увидел бы «не сопоставлено» при сопоставленном, а экран остался бы
    /// со старой версией редакции. Отказ уезжает в ответе признаком (<c>memory.failed</c>), причина — в журнал
    /// сервера.
    /// </summary>
    public static async Task<MatchMemory> SafelyAsync(PlanDbContext db, Func<Task<MatchMemory>> remember, int count, ILogger log)
    {
        try
        {
            return await remember();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Detach(db);
            log.LogWarning(failure, "Запомненное ({Kind}, {Count}) не записано, а сопоставление записано.", typeof(TMatch).Name, count);
            return new(0, 0, Failed: true);
        }
    }

    private static void Detach(PlanDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<TMatch>().ToList())
            entry.State = EntityState.Detached;
    }
}
