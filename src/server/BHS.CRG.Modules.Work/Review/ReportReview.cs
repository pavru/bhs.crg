using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>Тело приёмки: идентификатор изменения (повтор, ТЗ MOB-15) и время устройства — по желанию.</summary>
public sealed record AcceptRequest(Guid? ChangeId, DateTimeOffset? DeviceTime);

/// <summary>Тело возврата: что исправить — обязательно (ТЗ WORK-13).</summary>
public sealed record ReturnRequest(string? Comment, Guid? ChangeId, DateTimeOffset? DeviceTime);

/// <summary>
/// Приёмка и возврат поданного отчёта (ТЗ WORK-13, WORK-31.1, WORK-56, WORK-39; change <c>work-16-review</c>) — ход
/// записи по образцу подачи (<c>ReportSubmission</c>): снимок «до», номер под замком отчётов сотрудника, статус,
/// протокол и журнал отчёта — одной транзакцией; журнал ядра и уведомление — после фиксации, и их сбой решения не
/// отменяет.
///
/// <para><b>Порядок отказов:</b> версия не названа (400) → комментарий пуст (400, у возврата) → отчёт (404: чужая стройка
/// неотличима от несуществующего) → повтор того же <c>changeId</c> (200, если им применено то же решение; иначе 409
/// <see cref="ChangeTaken" />) → статус (409 <c>report-status</c>) → версия
/// (409). Статус раньше версии: принятый другим менеджером отчёт отвечает «уже принят», а не «обновите страницу».</para>
///
/// <para><b>«Ввёл и принял один человек»</b> (WORK-31.1) — приёмка проходит, а журнал получает отдельную запись
/// <see cref="ReportJournalKinds.SamePerson" />. <b>Уведомить о возврате некого</b> (нет учётной записи у сотрудника) —
/// запись <see cref="ReportJournalKinds.NotifyNone" /> тем же сохранением: журнал говорит, а не молчит. Обе берут
/// следующий номер последовательности: номер журнала уникален в пределах отчёта, а отчёт они не меняют — записи
/// протокола у них нет.</para>
/// </summary>
public sealed class ReportReview(
    WorkDbContext db, ReportReads reads, ReportViews views, TimeProvider time, ReportJournal journal, ReportPeople people,
    ReportActivity activity, ReturnNotice notice)
{
    /// <summary>
    /// Отказ повтора: этим <c>changeId</c> к отчёту применено другое изменение (правка, подача или другое решение). Ответ
    /// «повтор» здесь был бы неправдой — решение не принято; записать его с занятым идентификатором нельзя.
    /// </summary>
    public const string ChangeTaken =
        "Идентификатор изменения (changeId) уже занят другим изменением этого отчёта — у нового решения новый changeId.";

    public async Task<IResult> AcceptAsync(
        ReportViewer viewer, Guid id, AcceptRequest? body, HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        return await DecideAsync(viewer, id, seen, body?.ChangeId, body?.DeviceTime, comment: null, ct);
    }

    public async Task<IResult> ReturnAsync(
        ReportViewer viewer, Guid id, ReturnRequest? body, HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        var comment = ReportRules.ReturnComment(body?.Comment);
        return await DecideAsync(viewer, id, seen, body?.ChangeId, body?.DeviceTime, comment, ct);
    }

    /// <summary>Принять (<paramref name="comment" /> — <c>null</c>) или вернуть с комментарием.</summary>
    private async Task<IResult> DecideAsync(
        ReportViewer viewer, Guid id, string seen, Guid? changeId, DateTimeOffset? deviceTime, string? comment,
        CancellationToken ct)
    {
        var accept = comment is null;
        var kind = accept ? ReportChangeKind.Accepted : ReportChangeKind.Returned;
        // Автор — до транзакции: запрос к ядру не держит её открытой.
        var actor = await people.ActorAsync(viewer, ct);
        var change = changeId ?? Guid.NewGuid();
        Report report;
        string executor;
        Guid? recipient;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
            if (changeId is { } repeat && await reads.AppliedAsync(id, repeat, ct) is { } applied)
                return applied.Kind == kind
                    ? Results.Ok(await views.OneAsync(report, replayed: true, ct: ct))
                    : throw new ConflictException(ChangeTaken);
            report.EnsureReviewable();
            if (seen != db.VersionOf(report)) throw new ConflictException(ReportDesk.Stale);

            var employee = await people.EmployeeAsync(report.EmployeeId, ct);
            executor = employee?.FullName ?? report.EmployeeId.ToString();
            recipient = employee?.Account is { IsLinked: true, UserId: { } account } ? account : null;
            var samePerson = accept && ReportViews.AcceptWouldMark(report, people.UserId);

            var before = ReportJournal.Capture(report);
            var (seq, now) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow());
            if (accept) report.Accept(seq, now);
            else report.Return(seq, now);
            await db.SaveChangesAsync(ct);
            db.Add(ReportChange.Of(seq, report, change, kind, deviceTime, now, db.VersionOf(report)));
            journal.Append(accept ? ReportJournalKinds.Accepted : ReportJournalKinds.Returned, report, before, actor,
                executor, change, seq, deviceTime, now, comment);

            if (samePerson)
                await NoteAsync(ReportJournalKinds.SamePerson, report, actor, executor, change, deviceTime, now,
                    "ввёл и принял один человек", ct);
            if (!accept && recipient is null)
                await NoteAsync(ReportJournalKinds.NotifyNone, report, actor, executor, change, deviceTime, now,
                    NobodyToNotify(employee), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Правка монтажника или решение второго менеджера успели между чтением и записью. Решение другого — отказ по
            // статусу («уже принят»): обновление страницы его не отменит; правка — устаревшая версия.
            db.ChangeTracker.Clear();
            if (await reads.FindAsync(viewer, id, ct) is { Status: not ReportStatus.Submitted } decided)
                throw new ReportStatusException(decided.Status);
            throw new ConflictException(ReportDesk.Stale);
        }

        // Имя стройки — одно на журнал ядра и уведомление.
        var site = await activity.SiteNameAsync(report.ConstructionId);
        if (accept)
            await activity.RecordAsync(ReportActions.Accepted, report, executor, before: "подан", after: "принят", site);
        else
        {
            await activity.RecordAsync(ReportActions.Returned, report, executor, before: "подан", after: comment!, site);
            if (recipient is { } userId) await notice.PublishAsync(userId, report, site, comment!, actor.Name);
        }
        return Results.Ok(await views.OneAsync(report, ct: ct));
    }

    /// <summary>Отдельная запись журнала рядом с решением — своим номером, без записи протокола.</summary>
    private async Task NoteAsync(
        string kind, Report report, ReportJournalActor actor, string executor, Guid change, DateTimeOffset? deviceTime,
        DateTimeOffset now, string comment, CancellationToken ct)
    {
        var seq = await db.NextReportSeqAsync(report.EmployeeId, ct);
        journal.Append(kind, report, ReportJournal.Capture(report), actor, executor, change, seq, deviceTime, now, comment);
    }

    /// <summary>Почему уведомить о возврате некого — словами для журнала.</summary>
    private static string NobodyToNotify(ModuleEmployee? employee) => employee?.Account.State switch
    {
        null => "уведомить некого: сотрудник не найден в справочнике",
        ModuleAccountState.Deleted => "уведомить некого: учётная запись сотрудника удалена",
        _ => "уведомить некого: нет учётной записи",
    };
}
