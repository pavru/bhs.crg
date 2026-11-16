using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>
/// Сводка о поданных отчётах (ТЗ WORK-56, WORK-41, D16 зонтика; change <c>work-16-review</c>) — второй шаг задачи такта
/// автоподачи (<c>AutoSubmitJob</c>): одно личное уведомление каждому менеджеру стройки с действующим назначением
/// «менеджер».
///
/// <para><b>Что входит</b> — поданные отчёты, чьё окно подачи закрылось и чей момент подачи ещё не входил в сводку:
/// поданные вручную днём ждут полуночи стройки вместе с автоподанными — одна сводка, а не уведомление каждые пять минут.
/// Повторно поданный возвращённый несёт новый момент и входит в сводку, как только окно закрыто, — обычно ближайшим
/// тактом.</para>
///
/// <para><b>Кому</b> — учётным записям менеджеров стройки (<see cref="ManagerSites.ManagerAccountsAsync" />). Аудитория
/// правом разослала бы имена монтажников менеджерам всех строек; держатели <c>work.sites.all</c> без назначения сводку
/// не получают — у администратора было бы уведомление о каждой стройке компании. Отчёты стройки без менеджера
/// отмечаются тоже: менеджер, назначенный завтра, о вчерашних подачах не узнаёт колокольчиком — их покажет очередь.</para>
///
/// <para><b>Публикация, потом отметка</b> (<see cref="ReviewDigestMark" />): сбой между ними даёт повтор сводки, а не её
/// потерю. Сбой публикации одному менеджеру оставляет отчёты его строек неотмеченными — следующий такт повторит. Два
/// прогона разом разводит замок шага (<see cref="WorkDbContext.TryLockReviewDigestAsync" />): второй уходит ни с чем, и
/// сводка одна.</para>
/// </summary>
public sealed class SubmittedDigest(
    WorkDbContext db, ReportReads reads, ManagerSites sites, IModuleConstructions constructions,
    IModuleNotifications notifications, ILogger<SubmittedDigest> logger)
{
    /// <summary>Разослать сводку на часах <paramref name="now" />; итог — сколько отчётов отмечено.</summary>
    public async Task<int> RunAsync(ModuleReader system, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryLockReviewDigestAsync(ct)) return 0;
        var pending = await reads.DigestPendingAsync(system, now, ct);
        if (pending.Count == 0) return 0;

        var counts = pending.GroupBy(p => p.ConstructionId).ToDictionary(g => g.Key, g => g.Count());
        var managers = await sites.ManagerAccountsAsync([.. counts.Keys], ct);
        var names = (await constructions.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var failed = new HashSet<Guid>();
        foreach (var manager in managers.SelectMany(m => m.Value.Select(user => (User: user, Site: m.Key))).GroupBy(m => m.User))
        {
            var own = manager.Select(m => m.Site).Distinct()
                .OrderBy(s => names.GetValueOrDefault(s), StringComparer.CurrentCulture).ToList();
            try
            {
                await notifications.PublishAsync(ModuleNotificationLevel.Info,
                    $"Подано отчётов: {own.Sum(s => counts[s])}",
                    "Ждут приёмки: " + string.Join(", ", own.Select(s => $"{names.GetValueOrDefault(s) ?? "стройка"} — {counts[s]}")),
                    userId: manager.Key, ct: ct);
            }
            catch (Exception lost) when (lost is not OperationCanceledException)
            {
                logger.LogError(lost, "Сводка о поданных не ушла менеджеру {UserId} — повтор следующим тактом.", manager.Key);
                failed.UnionWith(own);
            }
        }

        var marked = 0;
        foreach (var report in pending.Where(p => !failed.Contains(p.ConstructionId)))
        {
            db.Add(ReviewDigestMark.Of(report.Id, report.SubmittedAt, now));
            marked++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return marked;
    }
}
