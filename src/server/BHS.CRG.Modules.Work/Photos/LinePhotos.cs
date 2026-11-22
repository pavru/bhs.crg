using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Work.Photos;

/// <summary>Фото в ответе загрузки: одно и то же у первой загрузки и у её повтора (ТЗ MOB-15).</summary>
/// <param name="State"><c>stored</c> — у ответа загрузки всегда: ответ даёт только пришедший файл.</param>
/// <param name="Size">Байты сжатого снимка, без превью.</param>
public sealed record LinePhotoDto(
    Guid Id, Guid ReportId, Guid LineId, string State, long? Size, int? Width, int? Height, DateTimeOffset? StoredAt);

/// <summary>
/// Загрузка, удаление и выдача фото строки (ТЗ WORK-33, WORK-34, WORK-39, WORK-11, MOB-15; change <c>work-17-photos</c>).
///
/// <para><b>Загрузка без <c>If-Match</c> — названное исключение.</b> Фото догоняют отчёт отдельной очередью (WORK-34),
/// и к их приходу версия отчёта сдвинута почти всегда: подачей, правкой менеджера, приёмкой. Загрузка ничего не
/// перезаписывает: идентификатор от клиента, повтор отвечает тем же, занятый другой строкой идентификатор — 409.
/// Удаление стирает данные и потому требует версию отчёта. Обе стороны перечислены в переписи адресов модуля
/// (<c>ReportVersionCensusTests</c>).</para>
///
/// <para><b>Порядок записи.</b> Всё дешёвое — до чтения тела: отчёт, строка, повтор, занятость, число фото. Затем порт
/// фото (<c>work-05</c>) сжимает и проверяет лимиты — сверх лимита в хранилище не пишется ничего. Затем файл и превью
/// кладутся портом модуля, и только потом ссылки — у строки фото, под замком отчётов сотрудника. Сбой между ними даёт
/// осиротевший файл, его находит поиск осиротевших; обратный порядок дал бы фото, которое не открывается. Удаление —
/// наоборот: сначала ссылка, потом файл.</para>
///
/// <para><b>Объявленное и новое фото</b> (WORK-11). Файл фото, которое строка уже назвала, — доставка: отчёт и его
/// версия не меняются, журнал молчит; номер изменения доставка берёт себе (<see cref="LinePhoto.StoredSeq" />), по нему
/// о ней узнаёт лента <c>?since</c>. Фото, которого строка не называла, — правка: номер изменения, запись протокола и
/// журнала; после подачи она «правка после подачи».</para>
///
/// <para><b>Снятое фото не возвращается</b> (MOB-15): файл, пришедший после того, как фото сняли со строки, получает 410
/// — по следу снятия (<see cref="LinePhotoRemoval" />) или по записи протокола, которой это фото когда-то добавили.
/// Клиент убирает такой файл из очереди.</para>
/// </summary>
public sealed class LinePhotos(
    WorkDbContext db, ReportReads reads, ReportViews views, ReportJournal journal, ReportPeople people,
    ReportActivity activity, IModulePhotos photos, IModuleSettings settings,
    [FromKeyedServices(WorkModule.ModuleCode)] IModuleBlobs blobs, TimeProvider time, ILogger<LinePhotos> logger,
    ReportPeriods periods)
{
    /// <summary>Длинная сторона хранимого снимка — та же, что у порта фото в его требованиях (<c>work-05</c>).</summary>
    public const int MaxSide = 2400;

    /// <summary>Длинная сторона превью — для галереи и очереди приёмки.</summary>
    public const int PreviewSide = 480;

    public const string IdTaken =
        "идентификатор фото уже занят фото другой строки — у нового фото идентификатор новый, его создаёт клиент.";

    public const string NotFoundText = "Фото не найдено: его удалили или идентификатор неверен.";

    public const string NotStored = "Фото ещё не загружено: строка его назвала, а файл пока не дошёл.";

    public const string LineNotFound = "Строка не найдена в отчёте: её удалили или идентификатор неверен.";

    public const string Removed = "Фото снято со строки — его файл больше не нужен и не принимается.";

    /// <summary>Предел числа фото на строку из настройки модуля.</summary>
    public static async Task<int> PerLineAsync(IModuleSettings settings, CancellationToken ct) =>
        (int)await settings.GetAsync(WorkSettings.PhotosPerLine, ct);

    public static string TooMany(int max) => $"не больше {max} фото на строку.";

    /// <summary>Загрузить файл фото строки отчёта, видимого читателю, — или ответить повтором.</summary>
    public async Task<IResult> ReceiveAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, Guid photoId, Stream body, CancellationToken ct)
    {
        if (photoId == Guid.Empty) throw new InvalidRequestException("Идентификатор фото пуст: его создаёт клиент.");

        // Дешёвое — до чтения тела: отказ и повтор не ждут, пока придут 15 МБ.
        var (report, line) = await TargetAsync(viewer, reportId, lineId, ct);
        if (await reads.PhotoLineAsync(photoId, ct) is { } holder && holder != lineId) throw new ConflictException(Capital(IdTaken));
        var declared = line.Photos.FirstOrDefault(p => p.Id == photoId);
        if (declared is null && await reads.PhotoRemovedAsync(photoId, ct)) return Gone();
        await DeliveryClosingAsync(await periods.CurrentAsync(ct), report, declared, ct);
        if (declared is { State: LinePhotoState.Stored }) return Results.Ok(Dto(report.Id, declared));
        var max = await PerLineAsync(settings, ct);
        if (declared is null && line.Photos.Count >= max) throw new InvalidRequestException(Capital(TooMany(max)));
        var employee = report.EmployeeId;
        // Автор и исполнитель нужны только правке (новое фото): доставке объявленного их не спрашиваем. Спрашиваем до
        // транзакции — запрос к ядру не держит её открытой.
        var who = declared is null ? await WhoAsync(viewer, employee, ct) : null;
        db.ChangeTracker.Clear();

        var megabytes = await settings.GetAsync(WorkSettings.PhotoMaxMegabytes, ct);
        var limits = new PhotoLimits((long)(megabytes * 1024 * 1024), MaxSide, PreviewSide, PhotoLimits.DefaultMaxPixels);
        var prepared = await photos.PrepareAsync(body, limits, ct);
        // Размер — 413, а не 400: он про транспорт, и код отличает «файл слишком велик» от «файл не тот».
        if (prepared is PhotoRefusal refusal)
            return refusal.Code switch
            {
                PhotoRefusalCode.TooLarge => TooLarge($"Фото больше {megabytes:0.##} МБ — предел задан в настройках модуля."),
                PhotoRefusalCode.TooManyPixels => TooLarge(refusal.Text),
                _ => throw new InvalidRequestException(refusal.Text),
            };
        // Итогов у порта два (PhotoResult): не отказ — значит, подготовленное фото.
        var ready = (PreparedPhoto)prepared;

        // Каждый положенный файл — в списке СРАЗУ: сбой на превью не оставляет снимок без ссылки.
        var put = new List<string>();
        var used = false;
        try
        {
            put.Add(await blobs.PutAsync($"{photoId}.jpg", new MemoryStream(ready.Image), FileKinds.Jpeg, ct));
            put.Add(await blobs.PutAsync($"{photoId}-preview.jpg", new MemoryStream(ready.Preview), FileKinds.Jpeg, ct));
            var files = new PhotoFiles(ready, put[0], put[1]);
            (var result, used) = await StoreAsync(viewer, reportId, lineId, photoId, files, employee, who, max, ct);
            return result;
        }
        finally
        {
            // Повтор, обогнавший этот запрос, и отказ под замком файлов не берут: ссылки на них нет.
            if (!used) await DropAsync(put);
        }
    }

    /// <summary>
    /// Ссылки на файл — под замком отчётов сотрудника и по отчёту, перечитанному под ним: параллельная загрузка того же
    /// фото успела бы иначе между проверкой и записью. Правку, прочитавшую фото ждущим до доставки, останавливает версия
    /// строки фото. <c>Used</c> — файлы взяты ссылкой.
    /// </summary>
    private async Task<(IResult Result, bool Used)> StoreAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, Guid photoId, PhotoFiles files, Guid employee, Who? who, int max,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var boundaries = await periods.UnderWriteLockAsync(ct);
        await db.LockReportsAsync(employee, ct);
        var (report, line) = await TargetAsync(viewer, reportId, lineId, ct);
        if (await reads.PhotoLineAsync(photoId, ct) is { } holder && holder != lineId) throw new ConflictException(Capital(IdTaken));

        var photo = line.Photos.FirstOrDefault(p => p.Id == photoId);
        // Снятое фото не возвращается ни в открытый, ни в закрытый отчёт.
        if (photo is null && (await reads.PhotoRemovedAsync(photoId, ct)
                              || await reads.AppliedAsync(reportId, photoId, ct) is not null))
            return (Gone(), false);
        var closingId = await DeliveryClosingAsync(boundaries, report, photo, ct);
        if (photo is { State: LinePhotoState.Stored }) return (Results.Ok(Dto(report.Id, photo)), false);
        var now = time.GetUtcNow();
        if (photo is not null)
        {
            // Объявленное фото — доставка (WORK-11): отчёт и его версия не меняются, журнал молчит. Номер — фото, для ленты.
            photo.Store(files, await db.NextReportSeqAsync(employee, ct), now, closingId);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (Results.Ok(Dto(report.Id, photo)), true);
        }

        if (line.Photos.Count >= max) throw new InvalidRequestException(Capital(TooMany(max)));
        // Объявленное фото сняли, пока шёл этот файл, и автора до транзакции не спросили — спрашиваем здесь.
        who ??= await WhoAsync(viewer, employee, ct);
        var kind = ReportDesk.ManagerEdits(viewer, report, who.Actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var seq = await db.NextReportSeqAsync(employee, ct);
        photo = LinePhoto.Arrived(photoId, lineId, files, seq, now);
        line.Photos.Add(photo);
        report.Touch(seq, now);
        kind = ReportJournal.AfterOwnEdit(viewer, report, before, now, kind);
        await db.SaveChangesAsync(ct);
        // Идентификатор изменения — идентификатор фото: его создал клиент, и второго применения ключ протокола не даст.
        db.Add(ReportChange.Of(seq, report, photoId, ReportChangeKind.Updated, null, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, who.Actor, who.Executor, photoId, seq, null, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await RecordAsync(kind, entry, report, who.Executor, "добавлено фото");
        return (Results.Ok(Dto(report.Id, photo)), true);
    }

    private async Task<Guid?> DeliveryClosingAsync(
        PeriodBoundaries boundaries, Report report, LinePhoto? photo, CancellationToken ct)
    {
        if (!ReportPeriods.IsClosed(boundaries, report.ConstructionId, report.Date)) return null;
        if (photo is not null && await periods.ClosingAsync(report, ct) is { } closing
            && photo.DeclaredBeforeClosing(closing.Id, closing.At))
            return closing.Id;

        throw ReportPeriods.ClosedReport(report.Id);
    }

    /// <summary>Автор правки и исполнитель отчёта — для журнала.</summary>
    private sealed record Who(ReportJournalActor Actor, string Executor);

    private async Task<Who> WhoAsync(ReportViewer viewer, Guid employee, CancellationToken ct) =>
        new(await people.ActorAsync(viewer, ct), await people.ExecutorAsync(employee, ct));

    /// <summary>
    /// Удалить фото строки (ТЗ WORK-33): отчёт и строка (404), период (409), затем <c>If-Match</c>,
    /// повтор с тем же <c>changeId</c> (200), фото (404), сверка версии (409).
    /// Ссылка уходит первой и оставляет след снятия, файл — после фиксации.
    /// </summary>
    public async Task<IResult> RemoveAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, Guid photoId, Guid? changeId, DateTimeOffset? deviceTime,
        HttpRequest request, CancellationToken ct)
    {
        var actor = await people.ActorAsync(viewer, ct);

        var holder = await reads.HolderOfIdAsync(reportId, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var boundaries = await periods.UnderWriteLockAsync(ct);
        await db.LockReportsAsync(holder.EmployeeId, ct);
        var (report, line) = await TargetAsync(viewer, reportId, lineId, ct);
        ReportPeriods.EnsureOpen(boundaries, report);
        var seen = SeenVersions.Required(request, "отчёта");
        var change = changeId ?? Guid.NewGuid();
        if (changeId is not null && await reads.AppliedAsync(reportId, change, ct) is not null)
            return Results.Ok(await views.OneAsync(viewer, report, replayed: true, ct: ct));
        var photo = line.Photos.FirstOrDefault(p => p.Id == photoId) ?? throw new NotFoundException(NotFoundText);
        if (seen != db.VersionOf(report)) throw new ConflictException(ReportDesk.Stale);

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var kind = ReportDesk.ManagerEdits(viewer, report, actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var (seq, now) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow());
        var paths = photo.Paths().ToList();
        line.Photos.Remove(photo);
        db.Add(LinePhotoRemoval.Of(photo, now));
        report.Touch(seq, now);
        kind = ReportJournal.AfterOwnEdit(viewer, report, before, now, kind);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, change, ReportChangeKind.Updated, deviceTime, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, actor, executor, change, seq, deviceTime, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await DropRemovedAsync(report, paths);
        await RecordAsync(kind, entry, report, executor, "снято фото");
        return Results.Ok(await views.OneAsync(viewer, report, ct: ct));
    }

    /// <summary>
    /// Файл фото или превью (ТЗ WORK-39, WORK-54): сначала отчёт фото читается с охватом читателя — не виден или
    /// черновик неавтору — 404, как несуществующее. Файла ещё нет — 404 с причиной. Метка называет сохранённый файл:
    /// повторное объявление фото с тем же идентификатором и новая загрузка дают другой путь и другую метку.
    /// </summary>
    public async Task<IResult> OpenAsync(
        ReportViewer viewer, Guid? userId, Guid photoId, bool preview, Guid? reportId, int? number,
        HttpContext http, CancellationToken ct)
    {
        var path = reportId is null && number is null
            ? await ActivePathAsync(viewer, userId, photoId, preview, ct)
            : await VersionPathAsync(viewer, userId, reportId, number, photoId, preview, ct);

        var tag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))}\"";
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = tag;
        if (http.Request.Headers.IfNoneMatch.Contains(tag))
        {
            // Отсутствующий объект не подтверждает старую копию клиента: его GET тоже ответил бы 404.
            if (await blobs.SizeAsync(path, ct) is null) throw new NotFoundException(NotFoundText);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        var content = await blobs.OpenAsync(path, ct);
        return Results.Stream(content, FileKinds.Jpeg);
    }

    private async Task<string> ActivePathAsync(
        ReportViewer viewer, Guid? userId, Guid photoId, bool preview, CancellationToken ct)
    {
        var photo = await reads.PhotoAsync(viewer, userId, photoId, ct) ?? throw new NotFoundException(NotFoundText);
        if (photo.State != LinePhotoState.Stored) throw new NotFoundException(NotStored);
        return preview ? photo.PreviewBlobPath! : photo.BlobPath!;
    }

    private async Task<string> VersionPathAsync(
        ReportViewer viewer, Guid? userId, Guid? reportId, int? number, Guid photoId, bool preview, CancellationToken ct)
    {
        if (reportId is not { } id || number is not { } versionNumber
            || await reads.VersionAsync(viewer, userId, id, versionNumber, ct) is not { } version
            || ReportVersionContent.Read(version.Content).Photos.FirstOrDefault(p => p.Id == photoId) is not { } photo)
            throw new NotFoundException(NotFoundText);
        return (preview ? photo.PreviewPath : photo.Path) ?? throw new NotFoundException(NotStored);
    }

    // У исправленного отчёта путь может остаться в снимке. Уборка хранилища сверяет снимки и текущие строки.
    internal Task DropRemovedAsync(Report report, IEnumerable<string> paths) =>
        report.CurrentVersion == 1 ? DropAsync(paths) : Task.CompletedTask;

    /// <summary>
    /// Удалить файлы, ссылки на которые уже сняты, — без права на отказ: правка состоялась, а сбой хранилища оставляет
    /// осиротевший файл, который найдёт поиск осиротевших.
    /// </summary>
    public async Task DropAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await blobs.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception lost)
            {
                logger.LogWarning(lost, "Файл фото {Path} не удалён: ссылка уже снята, файл найдёт поиск осиротевших.", path);
            }
        }
    }

    /// <summary>Отчёт, видимый читателю, с видимым содержимым (черновик — только автору, WORK-54), и его строка.</summary>
    private async Task<(Report Report, ReportLine Line)> TargetAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, CancellationToken ct)
    {
        var report = await reads.FindAsync(viewer, reportId, ct);
        if (report is null || !ReportReads.ContentVisible(report, viewer, people.UserId))
            throw new NotFoundException(ReportDesk.NotFoundText);
        var line = report.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException(LineNotFound);
        return (report, line);
    }

    /// <summary>Правка после подачи и правка менеджером — дублем в журнал действий ядра, после фиксации.</summary>
    private async Task RecordAsync(string kind, ReportJournalEntry entry, Report report, string executor, string what)
    {
        if (kind == ReportJournalKinds.ManagerEdited)
            await activity.RecordAsync(ReportActions.ManagerEdited, report, executor, before: null, after: what);
        else if (entry.AfterSubmission)
            await activity.RecordAsync(ReportActions.EditedAfterSubmission, report, executor, before: null, after: what);
    }

    private static LinePhotoDto Dto(Guid reportId, LinePhoto p) =>
        new(p.Id, reportId, p.LineId, LinePhoto.StateCode(p.State), p.Size, p.Width, p.Height, p.StoredAt);

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>410: фото сняли со строки, файл не нужен — клиент убирает его из очереди, а не повторяет.</summary>
    private static IResult Gone() => Results.Json(new { error = Removed }, statusCode: StatusCodes.Status410Gone);

    private static IResult TooLarge(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
