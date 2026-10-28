using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Persistence;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BHS.CRG.Infrastructure.Activity;

/// <summary>
/// Единственное место, где к набору записей журнала обращаются напрямую (issue #950).
///
/// ⚠️ Это не соглашение об оформлении: сторож <c>ActivityLogInventoryTests</c> перечисляет все
/// упоминания набора в исходниках и падает, если их стало больше. Проверки на уровне сущности
/// мало — прямой запрос к базе обходит и приватные сеттеры, и фабрику.
/// </summary>
public sealed class ActivityLog(AppDbContext db, IActivityActor actor, IActivityActionLookup actions,
    IAppSettingsStore settings, TimeProvider time) : IActivityLog
{
    /// <summary>Потолок страницы: журнал читают экраном, а не выгрузкой, и «take=100000» превратило
    /// бы чтение в способ выгрузить его целиком одним запросом.</summary>
    private const int MaxTake = 200;

    /// <summary>Потолок записей одного раскрытого ряда — первые по времени.</summary>
    public const int MaxSeries = 500;

    // Пояс компании — на время запроса: служба живёт в его области, а пояс за запрос не меняется.
    private TimeZoneInfo? _companyZone;

    /// <summary>
    /// Запись с сессией правки (ТЗ CORE-28.2): новая строка получает ключ сессии и признак головы
    /// серии, прежние строки не меняются.
    ///
    /// ⚠️ Запись с целью идёт под транзакционным замком ЦЕЛИ. Две записи одной цели, пришедшие почти
    /// одновременно (две вкладки, шапка и строки, второй человек), без замка прочитали бы одну и ту же
    /// «предыдущую» и склеились бы через чужую запись. Замок берёт и запись, которая сама не
    /// сворачивается: иначе сворачиваемая, пришедшая в тот же миг, прочитала бы предыдущую до её
    /// фиксации. Замок по одной цели, а не по таблице, — и отпускается с концом транзакции.
    ///
    /// Своя транзакция открывается, только если вызывающий не открыл её сам: тогда запись разделяет
    /// его судьбу — как и прежде.
    ///
    /// ⚠️ В ЧУЖОЙ транзакции замок берёт только сворачиваемый вид. Транзакционный замок держится до
    /// фиксации вызывающего, а тот пишет журнал по нескольким целям в своём порядке: два таких
    /// вызывающих, взяв замки X, Y и Y, X, упали бы взаимной блокировкой — отказом действия, которое
    /// раньше проходило. Плата — одиночная запись в чужой транзакции не видна сворачиваемой, пришедшей
    /// в тот же миг, и та может приклеиться к ряду через неё. Это видимость ряда, а не потеря записи.
    ///
    /// ⚠️ Сворачиваемый вид в транзакции со снимком (RepeatableRead, Serializable) — отказ: снимок взят
    /// до замка, и «предыдущая» запись читалась бы из прошлого, без той, ради которой замок ждали.
    /// </summary>
    public async Task RecordAsync(ActivityAction action, string? targetId = null, string? targetLabel = null,
        string? before = null, string? after = null, CancellationToken ct = default)
    {
        var who = actor.Current;
        var caller = db.Database.CurrentTransaction;
        if (targetId is null || (caller is not null && action.Fold is null))
        {
            db.ActivityRecords.Add(ActivityRecord.Create(
                action.Code, who.Id, who.Name, targetId, targetLabel, before, after, occurredAt: time.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return;
        }

        if (caller?.GetDbTransaction().IsolationLevel is IsolationLevel.RepeatableRead or IsolationLevel.Serializable
            or IsolationLevel.Snapshot)
            throw new InvalidOperationException(
                $"Сворачиваемое действие журнала «{action.Code}» пишут внутри транзакции с уровнем изоляции " +
                $"{caller.GetDbTransaction().IsolationLevel}. Её снимок взят до замка цели, и запись не увидела " +
                "бы предыдущую, сделанную за время ожидания, — ряд склеился бы через чужое событие. Пишите " +
                "журнал вне такой транзакции или на уровне ReadCommitted.");

        var own = caller is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({targetId}, 0))", ct);

            // Время — ПОСЛЕ замка: запись, ждавшая его, иначе оказалась бы раньше той, что его держала.
            var at = time.GetUtcNow();
            var (sessionId, head) = action.Fold is { } fold && who.Id is not null
                ? await SessionAsync(targetId, who.Id, fold, action.Code, at, ct)
                : (null, true);

            db.ActivityRecords.Add(ActivityRecord.Create(
                action.Code, who.Id, who.Name, targetId, targetLabel, before, after,
                occurredAt: at, sessionId: sessionId, seriesHead: head));
            await db.SaveChangesAsync(ct);

            if (own is not null) await own.CommitAsync(ct);
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    /// <summary>
    /// Продолжает ли запись сессию предыдущей записи цели, и если да — первая ли она своего вида в
    /// ней. <c>(null, true)</c> — новая сессия. Правило границы — <see cref="ActivitySession" />.
    /// </summary>
    private async Task<(Guid? SessionId, bool Head)> SessionAsync(string targetId, Guid? actorId,
        ActivityFold fold, string code, DateTimeOffset at, CancellationToken ct)
    {
        var previous = await db.ActivityRecords
            .Where(r => r.TargetId == targetId)
            .OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Action, r.ActorId, r.OccurredAt, r.SessionId,
                // Головы может не оказаться (журнал правили руками, копия пришла неполной) — тогда
                // сессия просто начинается заново, а не роняет запись действия.
                Started = db.ActivityRecords.Where(s => s.Id == r.SessionId)
                    .Select(s => (DateTimeOffset?)s.OccurredAt).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (previous?.Started is not { } started) return (null, true);

        var tail = new ActivitySessionTail(previous.ActorId, actions.Find(previous.Action)?.Fold?.Group,
            previous.OccurredAt, started);
        _companyZone ??= await settings.GetCompanyTimeZoneAsync(ct);
        if (!ActivitySession.Continues(actorId, fold.Group, at, tail, _companyZone)) return (null, true);

        // Подряд того же вида — почти каждая запись автосохранения: тогда вид в сессии уже есть, и
        // спрашивать базу незачем.
        var seen = previous.Action == code
            || await db.ActivityRecords.AnyAsync(r => r.SessionId == previous.SessionId && r.Action == code, ct);
        return (previous.SessionId, !seen);
    }

    /// <summary>
    /// Страница рядами (ТЗ CORE-28.2): головы серий, а к ним одним запросом — хвосты.
    ///
    /// ⚠️ Добивка по <c>Id</c> — не украшение (issue #980). Время у записи неуникально: массовая
    /// правка схемы или восстановление копии кладут несколько строк в одну миллисекунду, а без
    /// добивки порядок таких строк база вправе менять от запроса к запросу. Страницы тогда едут:
    /// вторая повторяет строки первой и прячет пограничные — то есть журнал молча не показывает
    /// запись, которая в нём есть.
    ///
    /// ⚠️ Страница идёт по ГОЛОВАМ, а не по последним записям рядов. Голова неизменна, поэтому
    /// страницы не едут, пока серия на первой растёт. Плата — ряд стоит по времени начала, и его
    /// последняя правка бывает позже соседнего ряда; интервал в ряду это объясняет.
    ///
    /// Видимость и отбор по действию верны и для хвостов: серия — записи одного вида, видимость
    /// задаётся видом, а сессия по построению не содержит записей другого автора или чужой группы.
    /// </summary>
    public async Task<IReadOnlyList<ActivityRow>> ReadAsync(int skip, int take, ActivityVisibility visible,
        string? action = null, CancellationToken ct = default)
    {
        var heads = await Filtered(visible, action)
            .Where(r => r.SeriesHead)
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxTake))
            .AsNoTracking()
            .ToListAsync(ct);
        if (heads.Count == 0) return [];

        // Все записи сессий страницы — одним запросом по индексу сессии. Записей другого вида той же
        // сессии здесь немного (группа — два-три вида), и разложить их по сериям дешевле в памяти.
        var sessions = heads.Select(h => h.SessionId).Distinct().ToList();
        var series = (await db.ActivityRecords
                .Where(r => sessions.Contains(r.SessionId))
                .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id)
                .AsNoTracking()
                .ToListAsync(ct))
            .ToLookup(r => (r.SessionId, r.Action));

        return [.. heads.Select(head => Row(head, series[(head.SessionId, head.Action)].ToList()))];
    }

    private ActivityRow Row(ActivityRecord head, IReadOnlyList<ActivityRecord> series)
    {
        // Голова в серии есть всегда, но страховка дешевле пропущенного ряда.
        if (series.Count == 0) series = [head];
        var last = series[^1];
        var (before, after) = ActivityFold.Merge(actions.Find(head.Action)?.Fold,
            [.. series.Select(r => (r.Before, r.After))]);

        return new ActivityRow(head.Id, head.OccurredAt, head.Action, head.ActorId, head.ActorName,
            last.TargetId, last.TargetLabel, before, after,
            series.Count, last.OccurredAt,
            head.TargetLabel != last.TargetLabel ? head.TargetLabel : null,
            head.SessionId);
    }

    public Task<int> CountAsync(ActivityVisibility visible, string? action = null,
        CancellationToken ct = default) =>
        Filtered(visible, action).Where(r => r.SeriesHead).CountAsync(ct);

    // Видимость — та же, что у страницы: ряд, скрытый от читающего, не раскрывается и по прямому адресу.
    // Потолок — тот же смысл, что у MaxTake: раскрытие ряда не должно становиться способом выгрузить
    // журнал одним запросом. Ряд знает своё число, и экран видит, что показана только часть.
    public async Task<IReadOnlyList<ActivityRecord>> SeriesAsync(Guid sessionId, string action,
        ActivityVisibility visible, CancellationToken ct = default) =>
        await Filtered(visible, action)
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id)
            .Take(MaxSeries)
            .AsNoTracking()
            .ToListAsync(ct);

    // Та же добивка и здесь: «последняя запись» при совпавшем времени иначе выбирается наугад, а по
    // ней сверяют состояние — и тогда наугад решается, записывать ли смену. Читается СЫРАЯ запись,
    // не ряд: сверке нужно последнее событие.
    public Task<ActivityRecord?> LastAsync(ActivityAction action, CancellationToken ct = default) =>
        db.ActivityRecords.Where(r => r.Action == action.Code)
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.Id)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ActivityRecord>> ExportAsync(CancellationToken ct = default) =>
        await db.ActivityRecords
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id)
            .AsNoTracking().ToListAsync(ct);

    public async Task<int> ImportAsync(IReadOnlyList<ActivityRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        var known = await db.ActivityRecords
            .Where(r => records.Select(x => x.Id).Contains(r.Id))
            .Select(r => r.Id)
            .ToHashSetAsync(ct);

        var fresh = records.Where(r => !known.Contains(r.Id)).ToList();
        if (fresh.Count == 0) return 0;

        db.ActivityRecords.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    // Видимость — в самом запросе, а не поверх страницы: отбор после Skip/Take дал бы страницы
    // разной длины и общее число, выдающее, сколько записей от читающего скрыто.
    private IQueryable<ActivityRecord> Filtered(ActivityVisibility visible, string? action)
    {
        var records = visible.Filter() is { } filter ? db.ActivityRecords.Where(filter) : db.ActivityRecords;
        return string.IsNullOrWhiteSpace(action) ? records : records.Where(r => r.Action == action);
    }
}
