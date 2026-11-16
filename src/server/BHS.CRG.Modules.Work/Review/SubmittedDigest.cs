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
/// <para><b>Публикация, потом отметка</b> (<see cref="ReviewDigestMark" />), одной транзакцией: сбой сохранения после
/// публикаций даёт повтор сводки, а не её потерю. <b>Сбой публикации одному менеджеру</b> отметку не задерживает — подача
/// уходит в повтор ему одному (<see cref="ReviewDigestRetry" />), и следующий такт кладёт её в его следующую сводку:
/// менеджеры, кому сводка дошла, второй раз её не получают. Повтор отпадает, когда отчёт решён или подан заново, или
/// менеджер больше не менеджер этой стройки. Два прогона разом разводит замок шага
/// (<see cref="WorkDbContext.TryLockReviewDigestAsync" />): второй уходит ни с чем, и сводка одна.</para>
/// </summary>
public sealed class SubmittedDigest(
    WorkDbContext db, ReportReads reads, ManagerSites sites, IModuleConstructions constructions,
    IModuleNotifications notifications, ILogger<SubmittedDigest> logger)
{
    /// <summary>Разослать сводку на часах <paramref name="now" />; итог — сколько подач отмечено.</summary>
    public async Task<int> RunAsync(ModuleReader system, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryLockReviewDigestAsync(ct)) return 0;
        var fresh = await reads.DigestPendingAsync(system, now, ct);
        var retries = await reads.DigestRetriesAsync(system, ct);
        if (fresh.Count == 0 && retries.Count == 0) return 0;

        var managers = await sites.ManagerAccountsAsync(
            [.. fresh.Select(p => p.ConstructionId).Concat(retries.Where(r => r.Live).Select(r => r.ConstructionId)).Distinct()], ct);
        bool Manages(Guid user, Guid site) => managers.TryGetValue(site, out var users) && users.Contains(user);

        // Кому что: новые подачи — всем менеджерам стройки, повторы — тому, кому не дошло, пока он менеджер стройки.
        var due = new Dictionary<Guid, Dictionary<Guid, Guid>>();
        void Owe(Guid user, Guid report, Guid site)
        {
            if (!due.TryGetValue(user, out var reports)) due[user] = reports = [];
            reports[report] = site;
        }
        foreach (var report in fresh)
            foreach (var user in managers.GetValueOrDefault(report.ConstructionId) ?? [])
                Owe(user, report.Id, report.ConstructionId);
        var owed = new List<ReviewDigestRetry>();
        foreach (var (row, site, live) in retries)
        {
            if (live && Manages(row.UserId, site))
            {
                Owe(row.UserId, row.ReportId, site);
                owed.Add(row);
            }
            else db.Remove(row);
        }

        var names = due.Count == 0 ? [] : (await constructions.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        foreach (var (user, reports) in due)
        {
            if (await SendAsync(user, reports, names, ct))
                db.RemoveRange(owed.Where(r => r.UserId == user));
            else
                foreach (var report in fresh.Where(p => reports.ContainsKey(p.Id)))
                    db.Add(ReviewDigestRetry.Of(user, report.Id, report.SubmittedAt));
        }

        foreach (var report in fresh) db.Add(ReviewDigestMark.Of(report.Id, report.SubmittedAt, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return fresh.Count;
    }

    /// <summary>Одна сводка менеджеру: число и стройки по алфавиту. Сбой — <c>false</c>: подачи уйдут повтором.</summary>
    private async Task<bool> SendAsync(
        Guid user, Dictionary<Guid, Guid> reports, Dictionary<Guid, string> names, CancellationToken ct)
    {
        var counts = reports.Values.GroupBy(site => site)
            .Select(g => (Name: names.GetValueOrDefault(g.Key) ?? "стройка", Count: g.Count()))
            .OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        try
        {
            await notifications.PublishAsync(ModuleNotificationLevel.Info,
                $"Подано отчётов: {reports.Count}",
                "Ждут приёмки: " + string.Join(", ", counts.Select(s => $"{s.Name} — {s.Count}")),
                userId: user, ct: ct);
            return true;
        }
        catch (Exception lost) when (lost is not OperationCanceledException)
        {
            logger.LogError(lost, "Сводка о поданных не ушла менеджеру {UserId} — повтор ему следующим тактом.", user);
            return false;
        }
    }
}
