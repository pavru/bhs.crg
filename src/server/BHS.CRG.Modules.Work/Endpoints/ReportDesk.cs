using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Devices;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Modules.Work.Endpoints;

internal sealed class ReportVersionConflictException() : ConflictException(ReportDesk.Stale);

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
/// <para>Пишущий адрес сначала находит отчёт в охвате читателя, затем проверяет период под замком и требует
/// <c>If-Match</c>. Повтор подачи открытого периода отвечает <c>alreadySubmitted</c> без сверки значения версии.</para>
/// </summary>
public sealed class ReportDesk(
    WorkDbContext db, ReportWriter writer, ReportReads reads, ReportViews views, TimeProvider time, ReportJournal journal,
    ReportPeople people, ReportActivity activity, ReportSubmission submission, RequiredFields required, LinePhotos photos,
    CrewInputs crew, ReportPeriods periods)
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
        if (viewer.FieldDelivery is not null)
        {
            _ = FieldChangeId(body.ChangeId);
            lines = WithWebDetails(lines, []);
        }
        var (actor, executor) = (await people.ActorAsync(viewer, ct), await people.ExecutorAsync(employeeId, ct));

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var boundaries = await periods.UnderWriteLockAsync(ct);
            if (viewer.FieldDelivery is { } delivery)
            {
                await delivery.LockAsync(ct);
                delivery.EnsureDate(employeeId, header.Date);
            }
            if (await reads.HolderOfIdAsync(id, ct) is { } holder)
                return await ReplayedOrTakenAsync(viewer, id, employeeId, holder, boundaries, ct);
            ReportPeriods.EnsureOpen(boundaries, header.ConstructionId, header.Date);
            await EnsureNotDeletedAsync(id, employeeId, ct);
            var (seq, now) = (await db.NextReportSeqAsync(employeeId, ct), time.GetUtcNow());
            var report = await writer.CreateAsync(id, employeeId, header, lines, seq, now, ct);
            if (enteredBy is var (userId, name)) report.EnteredBy(userId, name);
            await db.SaveChangesAsync(ct);
            var changeId = body.ChangeId ?? Guid.NewGuid();
            db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.Created, body.DeviceTime, now, db.VersionOf(report)));
            journal.Append(ReportJournalKinds.Created, report, null, actor, executor, changeId, seq, body.DeviceTime, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var created = await views.OneAsync(viewer, report, ct: ct);
            return Results.Created($"{path}/{id}", Body(viewer, created));
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Два заведения разом: проигравший видит запись победителя. Тот же id — повтор, та же четвёрка — ключ занят.
            db.ChangeTracker.Clear();
            await using var retry = await db.Database.BeginTransactionAsync(ct);
            var boundaries = await periods.UnderWriteLockAsync(ct);
            if (viewer.FieldDelivery is { } delivery)
            {
                await delivery.LockAsync(ct);
                delivery.EnsureDate(employeeId, header.Date);
            }
            if (await reads.HolderOfIdAsync(id, ct) is { } won)
                return await ReplayedOrTakenAsync(viewer, id, employeeId, won, boundaries, ct);
            ReportPeriods.EnsureOpen(boundaries, header.ConstructionId, header.Date);
            if (await reads.KeyHolderAsync(employeeId, header.ConstructionId, header.Date, header.Shift, id, ct) is { } key)
                throw new ReportKeyTakenException(key);
            await EnsureNotDeletedAsync(id, employeeId, ct);
            throw;
        }
    }

    /// <summary>Править отчёт, видимый читателю, — или ответить повтором уже применённой правки.</summary>
    public async Task<IResult> ChangeAsync(ReportViewer viewer, Guid id, ReportRequest body, HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await ApplyChangeAsync(viewer, id, body, request, ct);
        }
        catch (DbUpdateConcurrencyException) when (viewer.FieldDelivery is not null)
        {
            // Два устройства могли начать с одной версии. Победивший повтор уже виден, новый запрос устарел.
            db.ChangeTracker.Clear();
            return await ApplyChangeAsync(viewer, id, body, request, ct);
        }
    }

    private async Task<IResult> ApplyChangeAsync(
        ReportViewer viewer, Guid id, ReportRequest body, HttpRequest request, CancellationToken ct)
    {
        // Автор — до транзакции: запрос к ядру не держит её открытой.
        var actor = await people.ActorAsync(viewer, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var boundaries = await periods.UnderWriteLockAsync(ct);
        var delivery = viewer.FieldDelivery;
        if (delivery is not null) await delivery.LockAsync(ct);
        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(NotFoundText);
        delivery?.EnsureDate(report.EmployeeId, report.Date);
        if (delivery is null) ReportPeriods.EnsureOpen(boundaries, report);
        else _ = FieldChangeId(body.ChangeId);
        var applied = body.ChangeId is { } change ? await reads.AppliedAsync(id, change, ct) : null;
        if (delivery is not null && applied is not null && applied.Kind != ReportChangeKind.Updated)
            throw new FieldReportRefusal(409, "change-id-taken");
        var replayed = applied is not null;
        if (!replayed && delivery is not null)
        {
            ReportPeriods.EnsureOpen(boundaries, report);
            if (body.Date is { } date) delivery.EnsureDate(report.EmployeeId, date);
        }
        if (!replayed && body.ConstructionId is { } destination)
        {
            if (!viewer.Covers(destination, report.EmployeeId)) throw new NotFoundException(SiteNotYours);
            if (body.Date is { } date && ReportPeriods.IsClosed(boundaries, destination, date))
                throw new ReportPeriodClosedException("Период закрыт: перенести отчёт на эту дату нельзя.");
        }
        var seen = SeenVersions.Required(request, "отчёта");
        var changeId = body.ChangeId ?? throw new InvalidRequestException(
            "Не назван идентификатор изменения (changeId): его создаёт клиент, и повтор правки с ним не применится " +
            "второй раз.");
        if (body.Id is { } named && named != id)
            throw new InvalidRequestException("Идентификатор отчёта в теле (id) не совпадает с адресом.");
        if (body.EmployeeId is { } employee && employee != report.EmployeeId)
            throw new InvalidRequestException(
                "Сотрудника отчёта (employeeId) правкой не сменить: отчёт другого сотрудника — другой отчёт.");
        if (replayed)
            return await ReplyAsync(viewer, report, ct, replayed: true);
        if (seen != db.VersionOf(report)) throw VersionConflict(viewer);
        delivery?.EnsureOwnReportEdit(report, body.ConfirmManagerEdit);
        var header = body.Header();
        var lines = body.LineValues();
        if (delivery is not null) lines = WithWebDetails(lines, report.Lines);

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var kind = ManagerEdits(viewer, report, actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var changesCrew = report.Status is ReportStatus.Submitted or ReportStatus.Accepted
            && (report.Lines.Any(l => l.IsCrew) || lines.Any(l => l.IsCrew));
        if (changesCrew) await crew.LockAsync(report, ct);
        var (seq, now) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow());
        var dropped = await writer.ChangeAsync(report, header, lines, seq, now, ct);
        kind = ReportJournal.AfterOwnEdit(viewer, report, before, now, kind);
        if (changesCrew) await crew.LinesChangedAsync(report, before, ct);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.Updated, body.DeviceTime, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, actor, executor, changeId, seq, body.DeviceTime, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Файлы снятых фото — после фиксации: ссылка ушла первой (work-17).
        await photos.DropRemovedAsync(report, dropped);

        var changed = "изменено: " + ReportJournal.FieldsOf(entry);
        if (kind == ReportJournalKinds.ManagerEdited)
            await activity.RecordAsync(ReportActions.ManagerEdited, report, executor, before: null, after: changed);
        else if (entry.AfterSubmission)
            await activity.RecordAsync(ReportActions.EditedAfterSubmission, report, executor, before: null, after: changed);
        return await ReplyAsync(viewer, report, ct);
    }

    /// <summary>
    /// Подать отчёт, видимый читателю (ТЗ WORK-9, WORK-10, WORK-6, WORK-31). Момент первой подачи — время действия, но
    /// не позже конца окна (<see cref="SubmissionWindow.MomentOfManual" />); у повторной подачи возвращённого — время
    /// действия, но не позже часов сервера: окно к возврату уже прошло.
    /// </summary>
    public async Task<IResult> SubmitAsync(
        ReportViewer viewer, Guid id, SubmitRequest? body, HttpRequest request, CancellationToken ct)
    {
        var actor = await people.ActorAsync(viewer, ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var boundaries = await periods.UnderWriteLockAsync(ct);
            var delivery = viewer.FieldDelivery;
            if (delivery is not null) await delivery.LockAsync(ct);
            var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(NotFoundText);
            delivery?.EnsureDate(report.EmployeeId, report.Date);
            if (delivery is null) ReportPeriods.EnsureOpen(boundaries, report);
            var seen = SeenVersions.Required(request, "отчёта");
            if (delivery is not null)
            {
                var change = FieldChangeId(body?.ChangeId);
                if (await reads.AppliedAsync(id, change, ct) is { } applied)
                {
                    if (applied.Kind is not (ReportChangeKind.Submitted or ReportChangeKind.FieldReceipt))
                        throw new FieldReportRefusal(409, "change-id-taken");
                    return await ReplyAsync(viewer, report, ct, replayed: true, alreadySubmitted: true);
                }
                ReportPeriods.EnsureOpen(boundaries, report);
                if (seen != db.VersionOf(report)) throw new ReportVersionConflictException();
            }
            if (report.Status == ReportStatus.Submitted)
            {
                if (delivery is not null && report.SubmittedBy == ReportSubmitter.Auto && body?.DeviceTime is { } actedAt
                    && actedAt <= report.WindowEndsAt && !await reads.HasFieldReceiptAsync(id, ct))
                {
                    delivery.EnsureOwnReportEdit(report, body.ConfirmManagerEdit);
                    await submission.RecordFieldReceiptAsync(report, actor, await people.ExecutorAsync(report.EmployeeId, ct),
                        FieldChangeId(body.ChangeId), actedAt, time.GetUtcNow(), ct);
                    await tx.CommitAsync(ct);
                }
                return await ReplyAsync(viewer, report, ct, alreadySubmitted: true);
            }
            if (report.SubmitRefusal() is { } refusal) throw new ConflictException(refusal);
            if (seen != db.VersionOf(report)) throw VersionConflict(viewer);
            delivery?.EnsureOwnReportEdit(report, body?.ConfirmManagerEdit == true);
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
            return await ReplyAsync(viewer, report, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Автоподача (или вторая подача) успела между чтением и записью: поданный отчёт — повтор, иное — версия.
            db.ChangeTracker.Clear();
            if (viewer.FieldDelivery is not null)
                return await RetryFieldSubmitAsync(viewer, id, body?.ChangeId, ct);
            if (await reads.FindAsync(viewer, id, ct) is { } won)
            {
                ReportPeriods.EnsureOpen(await periods.CurrentAsync(ct), won);
                if (won.Status == ReportStatus.Submitted)
                    return Results.Ok(await views.OneAsync(viewer, won, alreadySubmitted: true, ct: ct));
            }
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
        var actor = await people.ActorAsync(viewer, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var boundaries = await periods.UnderWriteLockAsync(ct);
        if (await reads.FindAsync(viewer, id, ct) is not { } report)
        {
            if (changeId is { } repeat && await reads.AppliedAsync(id, repeat, ct) is { Kind: ReportChangeKind.Deleted } gone
                && viewer.Covers(gone.ConstructionId, gone.EmployeeId))
            {
                _ = SeenVersions.Required(request, "отчёта");
                return Results.NoContent();
            }
            throw new NotFoundException(NotFoundText);
        }
        ReportPeriods.EnsureOpen(boundaries, report);
        if (SeenVersions.Required(request, "отчёта") != db.VersionOf(report)) throw new ConflictException(Stale);
        if (report.Status != ReportStatus.Draft)
            throw new ConflictException("Удалить можно только черновик: поданный отчёт остаётся, его возвращают на правку.");
        if (report.CurrentVersion > 1)
            throw new ConflictException("Отчёт с историей исправлений удалить нельзя.");

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var before = ReportJournal.Capture(report);
        // Что удалено — до удаления: каскад снимает строки с отчёта при сохранении.
        var removed = $"часы: {(report.Hours is { } h ? h.ToString("0.##", CultureInfo.InvariantCulture) : "—")}, строк: {report.Lines.Count}";
        var files = report.Lines.SelectMany(l => l.Photos).SelectMany(p => p.Paths()).ToList();
        var (seq, now, change) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow(), changeId ?? Guid.NewGuid());
        db.Add(ReportChange.Of(seq, report, change, ReportChangeKind.Deleted, deviceTime, now, null));
        journal.Append(ReportJournalKinds.Deleted, report, before, actor, executor, change, seq, deviceTime, now);
        db.Remove(report);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await photos.DropAsync(files);

        await activity.RecordAsync(ReportActions.Deleted, report, executor, before: removed, after: "удалён");
        return Results.NoContent();
    }

    /// <summary>
    /// Правка менеджером (ТЗ WORK-14, <c>work-16</c>) — запасной ход: поданный или принятый отчёт правит адрес ввода за
    /// сотрудника (охват стройки, а не «свои»), и действует не сотрудник отчёта. Черновик, введённый за сотрудника, и
    /// возвращённый правятся как обычно: содержимое ещё не предъявлено или исправляется по просьбе менеджера.
    /// </summary>
    internal static bool ManagerEdits(ReportViewer viewer, Report report, ReportJournalActor actor) =>
        viewer.Reach.Kind != ModuleReachKind.Own && report.Status is ReportStatus.Submitted or ReportStatus.Accepted
                                                 && actor.EmployeeId != report.EmployeeId;

    /// <summary>
    /// Повтор заведения — тот же сотрудник, и отчёт виден читателю; иначе идентификатор занят. Ответ «занят» не
    /// несёт полей того отчёта: идентификатор — UUID от клиента, и подобрать чужой нельзя, а 404 на заведении читался
    /// бы как «адреса нет».
    /// </summary>
    private async Task<IResult> ReplayedOrTakenAsync(
        ReportViewer viewer, Guid id, Guid employeeId, (Guid EmployeeId, Guid ConstructionId) holder,
        PeriodBoundaries boundaries, CancellationToken ct)
    {
        if (holder.EmployeeId == employeeId && viewer.Covers(holder.ConstructionId, employeeId)
            && await reads.FindAsync(viewer, id, ct) is { } report)
        {
            if (viewer.FieldDelivery is { } delivery) delivery.EnsureDate(report.EmployeeId, report.Date);
            else ReportPeriods.EnsureOpen(boundaries, report);
            return await ReplyAsync(viewer, report, ct, replayed: true);
        }
        throw new ConflictException(IdTaken);
    }

    /// <summary>Идентификатор удалённого черновика не заводится снова: свой — «удалён», чужой — «занят».</summary>
    private async Task EnsureNotDeletedAsync(Guid id, Guid employeeId, CancellationToken ct)
    {
        if (await reads.DeletedHolderAsync(id, ct) is { } owner)
            throw new ConflictException(owner == employeeId ? Deleted : IdTaken);
    }

    private async Task<IResult> RetryFieldSubmitAsync(
        ReportViewer viewer, Guid id, Guid? changeId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await viewer.FieldDelivery!.LockAsync(ct);
        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(NotFoundText);
        viewer.FieldDelivery.EnsureDate(report.EmployeeId, report.Date);
        if (await reads.AppliedAsync(id, FieldChangeId(changeId), ct) is
            { Kind: ReportChangeKind.Submitted or ReportChangeKind.FieldReceipt })
            return await ReplyAsync(viewer, report, ct, replayed: true, alreadySubmitted: true);
        throw new ReportVersionConflictException();
    }

    private static Guid FieldChangeId(Guid? changeId) =>
        changeId is { } id && id != Guid.Empty ? id : throw new FieldReportRefusal(400, "invalid-request");

    private static IReadOnlyList<ReportLineValues> WithWebDetails(
        IReadOnlyList<ReportLineValues> incoming, IEnumerable<ReportLine> stored)
    {
        var details = stored.ToDictionary(line => line.Id, line => line.Details);
        return [.. incoming.Select(line => line with { Details = details.GetValueOrDefault(line.Id) })];
    }

    private static ConflictException VersionConflict(ReportViewer viewer) =>
        viewer.FieldDelivery is not null ? new ReportVersionConflictException() : new ConflictException(Stale);

    private async Task<IResult> ReplyAsync(
        ReportViewer viewer, Report report, CancellationToken ct, bool replayed = false, bool alreadySubmitted = false) =>
        Results.Ok(Body(viewer, await views.OneAsync(viewer, report, replayed, alreadySubmitted, ct)));

    private static object Body(ReportViewer viewer, ReportDto report) =>
        viewer.FieldDelivery is null ? report : FieldReportBody.Of(report);
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
