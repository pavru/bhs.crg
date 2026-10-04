using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Periods;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Periods;

/// <summary>
/// Закрытие учётного периода (ТЗ CORE-35; задача E1a, issue #1081).
///
/// <para><b>Гонка «запись против закрытия».</b> Модуль проверяет «период открыт» и пишет запись — и
/// между этими двумя шагами период могли закрыть: запись легла бы в закрытый месяц, не нарушив ни
/// одной проверки. Поэтому пишущий держит совещательный замок PostgreSQL разделяемым до конца своей
/// транзакции (<c>OpenPeriodWrite</c> в контрактах модулей), а закрытие и отмена берут его
/// ИСКЛЮЧИТЕЛЬНЫМ: они ждут, пока начатые записи зафиксируются, и новые ждут их.</para>
///
/// <para>⚠️ Ждёт закрытие недолго и отвечает отказом, а не тишиной: долгая транзакция модуля (импорт
/// выписки) иначе подвесила бы запрос закрытия на всё своё время, и человек нажал бы кнопку второй
/// раз.</para>
///
/// <para>⚠️ Событие в журнале ядра чисел модулей НЕ несёт — только контур и даты (см.
/// <see cref="ActivityActions.PeriodClosed" />).</para>
/// </summary>
public sealed class PeriodClosureService(
    AppDbContext db, IActivityLog journal, IActivityActor actor, IAppSettingsStore settings, TimeProvider time)
    : IPeriodClosures
{
    /// <summary>Сколько закрытие ждёт начатые записи модулей, прежде чем отказать.</summary>
    private const string LockTimeout = "5s";

    public async Task<PeriodLedger> LedgerAsync(CancellationToken ct = default) =>
        PeriodLedger.From(await db.PeriodClosures.AsNoTracking().ToListAsync(ct));

    public async Task<IReadOnlyList<Guid>> ConstructionsAsync(CancellationToken ct = default) =>
        await db.Constructions.AsNoTracking().OrderBy(c => c.Name).Select(c => c.Id).ToListAsync(ct);

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        // Пояс компании, а не стройки: собственный пояс стройки (ТЗ CORE-5) здесь не учитывается —
        // решение владельца от 04.10.2026. Пояс влияет только на «сегодня», даты периодов — дни.
        var zone = await settings.GetCompanyTimeZoneAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }

    public async Task<PeriodClosure> CloseAsync(ClosePeriod request, CancellationToken ct = default)
    {
        var label = await ContourLabelAsync(request.Contour, ct);
        var today = await TodayAsync(ct);
        var who = actor.Current;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOutWritersAsync(ct);

        var ledger = await LedgerAsync(ct);
        EnsureSeen(ledger, request.Contour, request.Seen);
        ledger.EnsureCanClose(request.Contour, request.From, request.Through, today);

        var before = ledger.ClosedThrough(request.Contour);
        var row = PeriodClosure.Close(
            request.Contour, request.From, request.Through, who.Id, who.Name, request.Reason, time.GetUtcNow());
        db.PeriodClosures.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await journal.RecordAsync(ActivityActions.PeriodClosed,
            targetId: TargetId(request.Contour), targetLabel: label,
            before: Boundary(before), after: Boundary(request.Through), ct: ct);
        return row;
    }

    public async Task<PeriodClosure> ReopenAsync(ReopenPeriod request, CancellationToken ct = default)
    {
        var label = await ContourLabelAsync(request.Contour, ct);
        var who = actor.Current;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOutWritersAsync(ct);

        var ledger = await LedgerAsync(ct);
        EnsureSeen(ledger, request.Contour, request.Seen);

        var before = ledger.ClosedThrough(request.Contour);
        var row = PeriodClosure.Reopen(
            ledger.Reopenable(request.Contour), who.Id, who.Name, request.Reason, time.GetUtcNow());
        db.PeriodClosures.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var after = (await LedgerAsync(ct)).ClosedThrough(request.Contour);
        await journal.RecordAsync(ActivityActions.PeriodReopened,
            targetId: TargetId(request.Contour), targetLabel: label,
            before: Boundary(before), after: $"{Boundary(after)}. Причина: {row.Reason}", ct: ct);
        return row;
    }

    public async Task<IReadOnlyList<PeriodClosure>> HistoryAsync(int take, CancellationToken ct = default) =>
        await db.PeriodClosures.AsNoTracking()
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PeriodClosure>> ExportAsync(CancellationToken ct = default) =>
        await db.PeriodClosures.AsNoTracking().OrderBy(r => r.At).ThenBy(r => r.Id).ToListAsync(ct);

    public async Task<int> ImportAsync(IReadOnlyList<PeriodClosure> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        var ids = records.Select(r => r.Id).ToList();
        var known = await db.PeriodClosures.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToHashSetAsync(ct);
        var fresh = records.Where(r => !known.Contains(r.Id)).ToList();
        if (fresh.Count == 0) return 0;

        db.PeriodClosures.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    /// <summary>
    /// Исключительный замок до конца транзакции. Вне транзакции он снялся бы тем же запросом и не
    /// защитил бы ничего — поэтому вызывается только после <c>BeginTransactionAsync</c>.
    /// </summary>
    private async Task LockOutWritersAsync(CancellationToken ct)
    {
        try
        {
            // lock_timeout — SET LOCAL: действует до конца транзакции и на соединение в пуле не уходит.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", ct);
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKeys.PeriodWrite})", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConflictException(
                "Сейчас в учёт идёт запись — закрытие периода ждёт её окончания и не дождалось. " +
                "Период не изменён. Повторите через минуту.");
        }
    }

    /// <summary>Граница, которую видел решающий, обязана быть нынешней.</summary>
    private static void EnsureSeen(PeriodLedger ledger, PeriodContour contour, DateOnly? seen)
    {
        var current = ledger.ClosedThrough(contour);
        if (current == seen) return;

        throw new ConflictException(
            $"Границу периода тем временем изменили: сейчас {Boundary(current)}. " +
            "Обновите страницу и повторите, если решение прежнее.");
    }

    /// <summary>Название контура для журнала. Заодно проверка, что стройка существует.</summary>
    private async Task<string> ContourLabelAsync(PeriodContour contour, CancellationToken ct)
    {
        if (contour.Kind == PeriodContourKind.Company) return "Компания";

        var name = await db.Constructions.Where(c => c.Id == contour.ConstructionId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        return name ?? throw new NotFoundException("Стройка не найдена: закрывать период нечему.");
    }

    private static string TargetId(PeriodContour contour) =>
        contour.ConstructionId?.ToString() ?? "company";

    private static string Boundary(DateOnly? through) =>
        through is { } date ? $"закрыто по {date:dd.MM.yyyy}" : "не закрыто ничего";
}
