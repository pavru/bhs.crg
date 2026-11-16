using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Общий ход заведения, правки и удаления отчёта (ТЗ MOB-15, MOB-16, MOB-17; change <c>work-13-report-model</c>) —
/// один на свои отчёты и на ввод за сотрудника. Различаются они правом, охватом (<see cref="ReportViewer" />) и тем,
/// откуда берётся сотрудник, а это решает адрес до вызова. Правила отчёта и строк — у <see cref="ReportWriter" />.
///
/// <para><b>Протокол повтора.</b> Заведение несёт <c>id</c> от клиента: тот же <c>id</c> того же сотрудника — 200 с
/// лежащим отчётом и <c>replayed: true</c>, чужой — 409 «идентификатор занят» без полей; идентификатор удалённого
/// черновика не заводится снова — 409. Правка несёт <c>changeId</c>: уже применённый — 200 по текущему состоянию с
/// ТЕКУЩЕЙ версией, без второго применения и без 409 за версию. Тело и версия ответа всегда одного состояния: версия
/// из протокола при более поздней правке разошлась бы с телом.</para>
///
/// <para><b>Номер и протокол.</b> Каждое изменение получает номер последовательности <c>work.report_seq</c> под замком
/// отчётов сотрудника (<see cref="WorkDbContext.NextReportSeqAsync" />) и запись <see cref="ReportChange" /> с временем
/// устройства и временем сервера; расхождение времён не отказ. Версия в протоколе — та, что ушла в ответе: поэтому
/// отчёт сохраняется раньше записи протокола, обе — одной транзакцией.</para>
///
/// <para><b>Журнал отчёта</b> (<see cref="ReportJournal" />, <c>work-14</c>) — тем же сохранением, что и запись
/// протокола, в той же транзакции: нет записи журнала — нет и правки (ТЗ WORK-51). Удаление черновика и правка после
/// подачи дублируются в журнал действий ядра ПОСЛЕ фиксации (<see cref="ReportActions" />): сбой там — предупреждение
/// в журнал приложения, а не откат, — свидетельство уже лежит в журнале отчёта.</para>
///
/// <para><b>Подача</b> (<c>work-15</c>) — тот же ход для своих отчётов и ввода за сотрудника. Порядок отказов: версия
/// не названа (400) → отчёт (404) → уже подан (200 <c>alreadySubmitted</c>, без сверки версии: изменения нет, и
/// потерять нечего) → другой статус (409) → версия (409) → обязательные поля (422). Конфликт версии строки при
/// сохранении — автоподача обогнала: перечитанный поданный отчёт отвечает тем же <c>alreadySubmitted</c>.</para>
///
/// <para><b>Порядок отказов правки:</b> запрос (400: версия, <c>changeId</c>, тело) → отчёт (404, чужой как
/// несуществующий) → сотрудник в теле (400) → повтор (200) → версия (409) → стройка вне охвата (404) → правила
/// записи (400/409).</para>
/// </summary>
public sealed class ReportDesk(
    WorkDbContext db, ReportWriter writer, ReportReads reads, ReportViews views, TimeProvider time, ReportJournal journal,
    ReportPeople people, ReportActivity activity, ReportSubmission submission, RequiredFields required)
{
    public const string IdTaken =
        "Идентификатор отчёта (id) уже занят — у нового отчёта идентификатор новый, его создаёт клиент.";

    public const string NotFoundText = "Отчёт не найден: его удалили или идентификатор неверен.";

    public const string SiteNotYours =
        "Стройка отчёта (constructionId) не найдена среди ваших строек: отчёт вводится за сотрудника только на стройке, " +
        "где вы менеджер.";

    public const string Deleted =
        "Отчёт с этим идентификатором (id) удалён — у нового отчёта идентификатор новый, его создаёт клиент.";

    public const string Stale =
        "Отчёт тем временем изменили, и эта правка не записана. Перечитайте отчёт и повторите: вы видели прежнее " +
        "состояние.";

    /// <summary>Завести отчёт сотрудника <paramref name="employeeId" /> или ответить повтором заведения.</summary>
    /// <param name="enteredBy">Автор ввода за сотрудника (ТЗ WORK-31); <c>null</c> — сотрудник заводит сам.</param>
    public async Task<IResult> CreateAsync(
        string path, ReportViewer viewer, Guid employeeId, ReportRequest body, (Guid UserId, string Name)? enteredBy,
        CancellationToken ct)
    {
        var id = body.Id ?? throw new InvalidRequestException(
            "Не назван идентификатор отчёта (id): его создаёт клиент, и повтор с ним же не заведёт второй отчёт.");
        if (id == Guid.Empty) throw new InvalidRequestException("Идентификатор отчёта (id) пуст: его создаёт клиент.");
        var header = body.Header();
        var lines = body.LineValues();
        if (await reads.HolderOfIdAsync(id, ct) is { } holder) return await ReplayedOrTakenAsync(viewer, id, employeeId, holder, ct);
        await EnsureNotDeletedAsync(id, employeeId, ct);
        var (actor, executor) = (await people.ActorAsync(viewer, ct), await people.ExecutorAsync(employeeId, ct));

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (seq, now) = (await db.NextReportSeqAsync(employeeId, ct), time.GetUtcNow());
            var report = await writer.CreateAsync(id, employeeId, header, lines, seq, now, ct);
            if (enteredBy is var (userId, name)) report.EnteredBy(userId, name);
            await db.SaveChangesAsync(ct);
            var changeId = body.ChangeId ?? Guid.NewGuid();
            db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.Created, body.DeviceTime, now, db.VersionOf(report)));
            journal.Append(ReportJournalKinds.Created, report, null, actor, executor, changeId, seq, body.DeviceTime, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"{path}/{id}", await views.OneAsync(report, ct: ct));
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Два заведения разом: проигравший видит запись победителя. Тот же id — повтор, та же четвёрка — ключ занят.
            db.ChangeTracker.Clear();
            if (await reads.HolderOfIdAsync(id, ct) is { } won) return await ReplayedOrTakenAsync(viewer, id, employeeId, won, ct);
            if (await reads.KeyHolderAsync(employeeId, header.ConstructionId, header.Date, header.Shift, id, ct) is { } key)
                throw new ReportKeyTakenException(key);
            await EnsureNotDeletedAsync(id, employeeId, ct);
            throw;
        }
    }

    /// <summary>Править отчёт, видимый читателю, — или ответить повтором уже применённой правки.</summary>
    public async Task<IResult> ChangeAsync(ReportViewer viewer, Guid id, ReportRequest body, HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        var changeId = body.ChangeId ?? throw new InvalidRequestException(
            "Не назван идентификатор изменения (changeId): его создаёт клиент, и повтор правки с ним не применится " +
            "второй раз.");
        if (body.Id is { } named && named != id)
            throw new InvalidRequestException("Идентификатор отчёта в теле (id) не совпадает с адресом.");
        var header = body.Header();
        var lines = body.LineValues();
        // Автор — до транзакции: запрос к ядру не держит её открытой.
        var actor = await people.ActorAsync(viewer, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(NotFoundText);
        if (body.EmployeeId is { } employee && employee != report.EmployeeId)
            throw new InvalidRequestException(
                "Сотрудника отчёта (employeeId) правкой не сменить: отчёт другого сотрудника — другой отчёт.");
        if (await reads.AppliedAsync(id, changeId, ct) is not null)
            return Results.Ok(await views.OneAsync(report, replayed: true, ct: ct));
        if (seen != db.VersionOf(report)) throw new ConflictException(Stale);
        if (!viewer.Covers(header.ConstructionId, report.EmployeeId)) throw new NotFoundException(SiteNotYours);

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var kind = ManagerEdits(viewer, report, actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var (seq, now) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow());
        await writer.ChangeAsync(report, header, lines, seq, now, ct);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.Updated, body.DeviceTime, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, actor, executor, changeId, seq, body.DeviceTime, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var changed = "изменено: " + ReportJournal.FieldsOf(entry);
        if (kind == ReportJournalKinds.ManagerEdited)
            await activity.RecordAsync(ReportActions.ManagerEdited, report, executor, before: null, after: changed);
        else if (entry.AfterSubmission)
            await activity.RecordAsync(ReportActions.EditedAfterSubmission, report, executor, before: null, after: changed);
        return Results.Ok(await views.OneAsync(report, ct: ct));
    }

    /// <summary>
    /// Подать отчёт, видимый читателю (ТЗ WORK-9, WORK-10, WORK-6, WORK-31). Момент первой подачи — время действия, но
    /// не позже конца окна (<see cref="SubmissionWindow.MomentOfManual" />); у повторной подачи возвращённого — время
    /// действия, но не позже часов сервера: окно к возврату уже прошло.
    /// </summary>
    public async Task<IResult> SubmitAsync(
        ReportViewer viewer, Guid id, SubmitRequest? body, HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        var actor = await people.ActorAsync(viewer, ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(NotFoundText);
            if (report.Status == ReportStatus.Submitted)
                return Results.Ok(await views.OneAsync(report, alreadySubmitted: true, ct: ct));
            if (report.SubmitRefusal() is { } refusal) throw new ConflictException(refusal);
            if (seen != db.VersionOf(report)) throw new ConflictException(Stale);
            var missing = await required.OfAsync(report, ct) ?? throw new WorkDirectoryMissingException(IModuleWorkTypes.NotSetUp);
            if (missing.Count > 0) throw new RequiredMissingException(missing);

            var executor = await people.ExecutorAsync(report.EmployeeId, ct);
            var now = time.GetUtcNow();
            // Верх момента — конец окна у черновика и часы сервера у возвращённого: телефон, ушедший вперёд, дал бы
            // подачу «из будущего», и правки до неё журнал не назвал бы правками после подачи. Раньше — можно: подача
            // без сети приходит позже.
            var acted = body?.DeviceTime ?? now;
            var at = report.Status == ReportStatus.Draft
                ? SubmissionWindow.MomentOfManual(acted, report.WindowEndsAt)
                : acted < now ? acted : now;
            await submission.ApplyAsync(report, at, ReportSubmitter.Manual, missing: null, actor, executor,
                body?.ChangeId ?? Guid.NewGuid(), body?.DeviceTime, now, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await views.OneAsync(report, ct: ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Автоподача (или вторая подача) успела между чтением и записью: поданный отчёт — повтор, иное — версия.
            db.ChangeTracker.Clear();
            if (await reads.FindAsync(viewer, id, ct) is { Status: ReportStatus.Submitted } won)
                return Results.Ok(await views.OneAsync(won, alreadySubmitted: true, ct: ct));
            throw new ConflictException(Stale);
        }
    }

    /// <summary>
    /// Удалить черновик. Удаление пишется в протокол записью <see cref="ReportChangeKind.Deleted" />: по ней курсор
    /// называет отчёт удалённым, а повтор удаления с тем же <c>changeId</c> получает 204, а не 404.
    /// </summary>
    public async Task<IResult> RemoveDraftAsync(
        ReportViewer viewer, Guid id, Guid? changeId, DateTimeOffset? deviceTime, HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        var actor = await people.ActorAsync(viewer, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await reads.FindAsync(viewer, id, ct) is not { } report)
        {
            if (changeId is { } repeat && await reads.AppliedAsync(id, repeat, ct) is { Kind: ReportChangeKind.Deleted } gone
                && viewer.Covers(gone.ConstructionId, gone.EmployeeId))
                return Results.NoContent();
            throw new NotFoundException(NotFoundText);
        }
        if (seen != db.VersionOf(report)) throw new ConflictException(Stale);
        if (report.Status != ReportStatus.Draft)
            throw new ConflictException("Удалить можно только черновик: поданный отчёт остаётся, его возвращают на правку.");

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var before = ReportJournal.Capture(report);
        // Что удалено — до удаления: каскад снимает строки с отчёта при сохранении.
        var removed = $"часы: {(report.Hours is { } h ? h.ToString("0.##", CultureInfo.InvariantCulture) : "—")}, строк: {report.Lines.Count}";
        var (seq, now, change) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow(), changeId ?? Guid.NewGuid());
        db.Add(ReportChange.Of(seq, report, change, ReportChangeKind.Deleted, deviceTime, now, null));
        journal.Append(ReportJournalKinds.Deleted, report, before, actor, executor, change, seq, deviceTime, now);
        db.Remove(report);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await activity.RecordAsync(ReportActions.Deleted, report, executor, before: removed, after: "удалён");
        return Results.NoContent();
    }

    /// <summary>
    /// Правка менеджером (ТЗ WORK-14, <c>work-16</c>) — запасной ход: поданный отчёт правит адрес ввода за сотрудника
    /// (охват стройки, а не «свои»), и действует не сотрудник отчёта. Черновик, введённый за сотрудника, правится как
    /// обычно: до подачи содержимое ещё не предъявлено.
    /// </summary>
    private static bool ManagerEdits(ReportViewer viewer, Report report, ReportJournalActor actor) =>
        viewer.Reach.Kind != ModuleReachKind.Own && report.Status == ReportStatus.Submitted
                                                 && actor.EmployeeId != report.EmployeeId;

    /// <summary>
    /// Повтор заведения — тот же сотрудник, и отчёт виден читателю; иначе идентификатор занят. Ответ «занят» не
    /// несёт полей того отчёта: идентификатор — UUID от клиента, и подобрать чужой нельзя, а 404 на заведении читался
    /// бы как «адреса нет».
    /// </summary>
    private async Task<IResult> ReplayedOrTakenAsync(
        ReportViewer viewer, Guid id, Guid employeeId, (Guid EmployeeId, Guid ConstructionId) holder, CancellationToken ct)
    {
        if (holder.EmployeeId == employeeId && viewer.Covers(holder.ConstructionId, employeeId)
            && await reads.FindAsync(viewer, id, ct) is { } report)
            return Results.Ok(await views.OneAsync(report, replayed: true, ct: ct));
        throw new ConflictException(IdTaken);
    }

    /// <summary>Идентификатор удалённого черновика не заводится снова: свой — «удалён», чужой — «занят».</summary>
    private async Task EnsureNotDeletedAsync(Guid id, Guid employeeId, CancellationToken ct)
    {
        if (await reads.DeletedHolderAsync(id, ct) is { } owner)
            throw new ConflictException(owner == employeeId ? Deleted : IdTaken);
    }
}

/// <summary>
/// Отказы пути записи, у которых в ответе есть поля сверх текста: занятый ключ отчёта — с идентификатором
/// существующего (клиент открывает его, а не заводит второй), справочник не заведён — с кодом (ТЗ CORE-20.4),
/// подача без обязательных полей — 422 с перечнем строк и полей (ТЗ WORK-6), решение не по статусу — 409 с кодом и
/// статусом (ТЗ WORK-13, <c>work-16</c>).
/// </summary>
internal static class ReportRefusals
{
    public static async Task<IResult> RunAsync(Func<Task<IResult>> act)
    {
        try
        {
            return await act();
        }
        catch (ReportKeyTakenException taken)
        {
            return Results.Conflict(new { error = taken.Message, existingId = taken.ExistingId });
        }
        catch (WorkDirectoryMissingException missing)
        {
            return Results.Conflict(new { code = missing.Code, error = missing.Message });
        }
        catch (ReportStatusException refused)
        {
            // Статус, а не версия (work-16): кодом причины отказ отличается от «отчёт тем временем изменили».
            return Results.Conflict(new
            {
                code = ReportStatusException.ReasonCode, status = Report.StatusCode(refused.Status), error = refused.Message,
            });
        }
        catch (RequiredMissingException missing)
        {
            return Results.UnprocessableEntity(new
            {
                error = missing.Message,
                requiredMissing = missing.Missing.Select(m => new { line = m.Line, lineId = m.LineId, field = m.Field }),
            });
        }
    }
}
