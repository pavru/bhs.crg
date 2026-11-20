using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
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
/// <para><b>Объявленное и новое фото</b> (WORK-11). Файл фото, которое строка уже назвала, — доставка: отчёт не
/// меняется, журнал молчит. Фото, которого строка не называла, — правка: номер изменения, запись протокола и журнала;
/// после подачи она «правка после подачи».</para>
/// </summary>
public sealed class LinePhotos(
    WorkDbContext db, ReportReads reads, ReportViews views, ReportJournal journal, ReportPeople people,
    ReportActivity activity, IModulePhotos photos, IModuleSettings settings,
    [FromKeyedServices(WorkModule.ModuleCode)] IModuleBlobs blobs, TimeProvider time, ILogger<LinePhotos> logger)
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
        if (line.Photos.FirstOrDefault(p => p.Id == photoId) is { State: LinePhotoState.Stored } done)
            return Results.Ok(Dto(report.Id, done));
        var max = await PerLineAsync(settings, ct);
        if (line.Photos.All(p => p.Id != photoId) && line.Photos.Count >= max) throw new InvalidRequestException(Capital(TooMany(max)));
        var (employee, actor) = (report.EmployeeId, await people.ActorAsync(viewer, ct));
        var executor = await people.ExecutorAsync(employee, ct);
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

        var files = new PhotoFiles(ready,
            await blobs.PutAsync($"{photoId}.jpg", new MemoryStream(ready.Image), "image/jpeg", ct),
            await blobs.PutAsync($"{photoId}-preview.jpg", new MemoryStream(ready.Preview), "image/jpeg", ct));
        var used = false;
        try
        {
            (var result, used) = await StoreAsync(viewer, reportId, lineId, photoId, files, employee, actor, executor, max, ct);
            return result;
        }
        finally
        {
            // Повтор, обогнавший этот запрос, и отказ под замком файлов не берут: ссылки на них нет.
            if (!used) await DropAsync([files.BlobPath, files.PreviewBlobPath]);
        }
    }

    /// <summary>
    /// Ссылки на файл — под замком отчётов сотрудника и по отчёту, перечитанному под ним: параллельная загрузка того же
    /// фото или правка строки успели бы иначе между проверкой и записью. <c>Used</c> — файлы взяты ссылкой.
    /// </summary>
    private async Task<(IResult Result, bool Used)> StoreAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, Guid photoId, PhotoFiles files, Guid employee,
        ReportJournalActor actor, string executor, int max, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockReportsAsync(employee, ct);
        var (report, line) = await TargetAsync(viewer, reportId, lineId, ct);
        if (await reads.PhotoLineAsync(photoId, ct) is { } holder && holder != lineId) throw new ConflictException(Capital(IdTaken));

        var now = time.GetUtcNow();
        var photo = line.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo is { State: LinePhotoState.Stored }) return (Results.Ok(Dto(report.Id, photo)), false);
        if (photo is not null)
        {
            // Объявленное фото — доставка (WORK-11): отчёт и его версия не меняются, журнал молчит.
            photo.Store(files, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (Results.Ok(Dto(report.Id, photo)), true);
        }

        if (line.Photos.Count >= max) throw new InvalidRequestException(Capital(TooMany(max)));
        var kind = ReportDesk.ManagerEdits(viewer, report, actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var seq = await db.NextReportSeqAsync(employee, ct);
        photo = LinePhoto.Arrived(photoId, lineId, files, now);
        line.Photos.Add(photo);
        report.Touch(seq, now);
        await db.SaveChangesAsync(ct);
        // Идентификатор изменения — идентификатор фото: его создал клиент, и второго применения ключ протокола не даст.
        db.Add(ReportChange.Of(seq, report, photoId, ReportChangeKind.Updated, null, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, actor, executor, photoId, seq, null, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await RecordAsync(kind, entry, report, executor, "добавлено фото");
        return (Results.Ok(Dto(report.Id, photo)), true);
    }

    /// <summary>
    /// Удалить фото строки (ТЗ WORK-33): <c>If-Match</c> с версией отчёта — первым, затем отчёт, строка и фото (404),
    /// повтор с тем же <c>changeId</c> (200 по текущему состоянию), версия (409). Ссылка уходит первой, файл — после
    /// фиксации.
    /// </summary>
    public async Task<IResult> RemoveAsync(
        ReportViewer viewer, Guid reportId, Guid lineId, Guid photoId, Guid? changeId, DateTimeOffset? deviceTime,
        HttpRequest request, CancellationToken ct)
    {
        var seen = SeenVersions.Required(request, "отчёта");
        var actor = await people.ActorAsync(viewer, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (report, line) = await TargetAsync(viewer, reportId, lineId, ct);
        var change = changeId ?? Guid.NewGuid();
        if (line.Photos.FirstOrDefault(p => p.Id == photoId) is not { } photo)
        {
            if (changeId is not null && await reads.AppliedAsync(reportId, change, ct) is not null)
                return Results.Ok(await views.OneAsync(viewer, report, replayed: true, ct: ct));
            throw new NotFoundException(NotFoundText);
        }
        if (seen != db.VersionOf(report)) throw new ConflictException(ReportDesk.Stale);

        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var kind = ReportDesk.ManagerEdits(viewer, report, actor) ? ReportJournalKinds.ManagerEdited : ReportJournalKinds.Updated;
        var before = ReportJournal.Capture(report);
        var (seq, now) = (await db.NextReportSeqAsync(report.EmployeeId, ct), time.GetUtcNow());
        var paths = photo.Paths().ToList();
        line.Photos.Remove(photo);
        report.Touch(seq, now);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, change, ReportChangeKind.Updated, deviceTime, now, db.VersionOf(report)));
        var entry = journal.Append(kind, report, before, actor, executor, change, seq, deviceTime, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await DropAsync(paths);
        await RecordAsync(kind, entry, report, executor, "снято фото");
        return Results.Ok(await views.OneAsync(viewer, report, ct: ct));
    }

    /// <summary>
    /// Файл фото или превью (ТЗ WORK-39, WORK-54): сначала отчёт фото читается с охватом читателя — не виден или
    /// черновик неавтору — 404, как несуществующее. Файла ещё нет — 404 с причиной. Содержимое по идентификатору не
    /// меняется, поэтому повторный запрос с тем же <c>If-None-Match</c> получает 304 — но тоже после проверки доступа.
    /// </summary>
    public async Task<IResult> OpenAsync(
        ReportViewer viewer, Guid? userId, Guid photoId, bool preview, HttpContext http, CancellationToken ct)
    {
        var photo = await reads.PhotoAsync(viewer, userId, photoId, ct) ?? throw new NotFoundException(NotFoundText);
        if (photo.State != LinePhotoState.Stored) throw new NotFoundException(NotStored);

        var tag = $"\"{photo.Id:N}{(preview ? "-p" : "")}\"";
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = tag;
        if (http.Request.Headers.IfNoneMatch.Contains(tag)) return Results.StatusCode(StatusCodes.Status304NotModified);
        var content = await blobs.OpenAsync(preview ? photo.PreviewBlobPath! : photo.BlobPath!, ct);
        return Results.Stream(content, "image/jpeg");
    }

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

    private static IResult TooLarge(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
