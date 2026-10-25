using System.Net.Mime;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Адреса импорта сметы из ГРАНД-Сметы (change <c>plan-07-estimate-import</c>, ТЗ PLAN-6, PLAN-7, D6 зонтика
/// <c>plan-00-epic</c>): загрузка с разбором в фоне, отчёт разбора, отмена.
///
/// <para><b>Ворота.</b> Все адреса — под <c>plan.estimate.edit</c>: импорт — правка сметы, и отчёт его читает
/// тот, кто импортирует (ТЗ PLAN-2, AUTH-8). Итог в отчёте — только с <c>plan.estimate.cost</c>.</para>
///
/// <para><b>Версия.</b> Отмена называет версию записи импорта заголовком <c>If-Match</c>: без него — 400, с
/// устаревшей — 409. Заведение импорта версии не называет: записи ещё нет.</para>
/// </summary>
public static class EstimateImportEndpoints
{
    /// <summary>Предел файла — тот же, что у прочих файлов модулей: он же достижим на всём пути запроса.</summary>
    public const long MaxFileBytes = FileKindCatalog.MaxBytes;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var imports = endpoints.MapGroup("/api/plan/imports").WithTags("Импорт смет");
        var edit = AppPolicies.Permission(PlanModule.EstimateEdit);

        imports.MapPost("/", CreateAsync).RequireAuthorization(edit).DisableAntiforgery();
        imports.MapGet("/{id:guid}", GetAsync).RequireAuthorization(edit);
        imports.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(edit);
    }

    /// <summary>
    /// Загрузить файл: форма <c>file</c>, <c>constructionId</c> и куда — <c>estimateId</c> (новая редакция этой
    /// сметы) либо <c>newEstimate.name</c>, <c>newEstimate.sectionId</c>, <c>newEstimate.kind</c> (новая смета,
    /// решение В-12 (а)). Ответ — импорт в состоянии <c>parsing</c>; разбор идёт фоновой операцией.
    ///
    /// <para>Порядок — файл, потом запись, потом очередь (<c>IModuleBlobs</c>): упавшее между первыми двумя даёт
    /// файл без ссылки, которую найдёт поиск осиротевших, а не запись, которая обещает файл и не открывает его.</para>
    /// </summary>
    private static async Task<IResult> CreateAsync(
        IFormFile file, HttpRequest request, PlanDbContext db, IModuleUser user, IModuleConstructions constructions,
        IModuleJobs jobs, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs, ILoggerFactory logs,
        CancellationToken ct)
    {
        if (file.Length == 0) throw new InvalidRequestException("Файл пуст — импортировать нечего.");
        if (file.Length > MaxFileBytes)
            throw new InvalidRequestException($"Файл больше {MaxFileBytes / (1024 * 1024)} МБ — это не файл сметы.");

        var form = request.Form;
        var siteId = FormGuid(form, "constructionId")
            ?? throw new InvalidRequestException("Не названа стройка (constructionId): смета импортируется в стройку.");
        var site = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == siteId)
            ?? throw new InvalidRequestException("Стройки нет — её удалили либо адрес неверен.");
        var estimateId = FormGuid(form, "estimateId");
        var newName = Text(form, "newEstimate.name");
        if (estimateId is null == newName is null)
            throw new InvalidRequestException(
                "Не выбрано, куда ляжет редакция: назовите либо смету (estimateId) — будет её новая редакция, либо " +
                "новую смету (newEstimate.name) — не то и другое сразу.");

        Estimate? estimate = null;
        if (estimateId is { } existing)
        {
            estimate = await db.Estimates.AsNoTracking().FirstOrDefaultAsync(e => e.Id == existing, ct)
                ?? throw new InvalidRequestException("Сметы нет — её удалили либо адрес неверен.");
            if (estimate.ConstructionId != site.Id)
                throw new InvalidRequestException($"Смета «{estimate.Name}» — другой стройки, не «{site.Name}».");
        }
        var sectionId = FormGuid(form, "newEstimate.sectionId");
        if (sectionId is { } section && site.Sections.All(s => s.Id != section))
            throw new InvalidRequestException($"Раздела нет у стройки «{site.Name}» — смета на раздел составляется в его стройке.");
        var kind = EstimateEndpoints.Kind(Text(form, "newEstimate.kind"));
        if (estimate is null) Estimate.CheckedName(newName);

        // Тип — как его назвал клиент: вид файла здесь не решается, его определяет разбор по содержимому.
        // Своего перечня типов рядом с реестром ядра у модуля нет (FileKindCatalog): файл сметы — не вложение,
        // которое показывают и распознают.
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Application.Octet : file.ContentType;
        string path;
        await using (var content = file.OpenReadStream())
            path = await blobs.PutAsync(file.FileName, content, contentType, ct);

        var now = DateTimeOffset.UtcNow;
        var import = estimate is not null
            ? EstimateImport.IntoEstimate(estimate, path, file.FileName, user.Id, user.Name, now)
            : EstimateImport.IntoNewEstimate(site.Id, newName!, sectionId, kind, path, file.FileName, user.Id, user.Name, now);
        try
        {
            db.EstimateImports.Add(import);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Импорт не сохранился — файл остался бы в хранилище ничьим. Уборка причину не подменяет.
            try { await blobs.DeleteAsync(path, CancellationToken.None); }
            catch (Exception) { }
            throw;
        }

        // ⚠️ Импорт ЗАВЕДЁН — с этой строки ответом может быть только он: отказ после сохранения оставил бы
        // человека с ошибкой и импортом, о котором он не знает. Поэтому дальше — без токена отмены.
        var none = CancellationToken.None;
        await QueueAsync(db, jobs, import, logs.CreateLogger(typeof(EstimateImportEndpoints)), none);
        return Results.Created($"/api/plan/imports/{import.Id}", await EstimateImportViews.OneAsync(db, user, jobs, import.Id, none));
    }

    /// <summary>
    /// Поставить разбор. Отказ очереди не теряется: импорт переходит в отказ с её словами, и отчёт скажет,
    /// почему разбора нет, — вместо «разбирается», которое не кончится никогда.
    /// </summary>
    private static async Task QueueAsync(PlanDbContext db, IModuleJobs jobs, EstimateImport import, ILogger logger, CancellationToken none)
    {
        string why;
        try
        {
            var jobId = await jobs.EnqueueAsync(EstimateImportJob.OperationCode, import.Id, $"Разбор сметы «{import.FileName}»", ct: none);
            // Номер задачи — только если его ещё нет: задача, успевшая отработать раньше, вписала его сама.
            await db.EstimateImports.Where(i => i.Id == import.Id && i.JobId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.JobId, jobId), none);
            return;
        }
        catch (DomainException refused)
        {
            why = $"Разбор не поставлен в очередь: {refused.Message}";
        }
        catch (Exception crash)
        {
            logger.LogError(crash, "Импорт {ImportId} заведён, но его разбор поставить не удалось.", import.Id);
            why = "Разбор не поставлен в очередь: внутренняя ошибка. Отмените импорт и загрузите файл ещё раз; если " +
                  "повторится — подробности в журнале сервера.";
        }

        db.ChangeTracker.Clear();
        var stored = await db.EstimateImports.FirstAsync(i => i.Id == import.Id, none);
        if (stored.State != EstimateImportState.Parsing) return;
        stored.Reject(EstimateImport.FailedCode, why, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(none);
    }

    private static async Task<IResult> GetAsync(
        Guid id, PlanDbContext db, IModuleUser user, IModuleJobs jobs, CancellationToken ct) =>
        Results.Ok(await EstimateImportViews.OneAsync(db, user, jobs, id, ct));

    /// <summary>
    /// Отменить импорт, из которого редакция не создана: запись уходит, файл удаляется. Отменить можно и идущий
    /// разбор — задача, найдя импорт отменённым, выбросит разобранное.
    ///
    /// <para>Сначала запись, потом файл: упавшее между ними оставит файл без ссылки — его найдёт поиск
    /// осиротевших, а запись без файла обещала бы отчёт, которого уже не перечитать.</para>
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, HttpRequest request, PlanDbContext db, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs,
        ILoggerFactory logs, CancellationToken ct)
    {
        var import = await db.EstimateImports.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw EstimateImportViews.NotFound();
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(import), "Импорт");
        import.EnsureCancellable();

        db.EstimateImports.Remove(import);
        await db.SaveChangesAsync(ct);
        await DeleteFileAsync(blobs, import, logs.CreateLogger(typeof(EstimateImportEndpoints)));
        return Results.NoContent();
    }

    /// <summary>Убрать файл ушедшего импорта. Не удалось — не отказ: импорта уже нет, файл найдёт уборка.</summary>
    internal static async Task DeleteFileAsync(IModuleBlobs blobs, EstimateImport import, ILogger logger)
    {
        try
        {
            await blobs.DeleteAsync(import.BlobPath, CancellationToken.None);
        }
        catch (Exception lost)
        {
            logger.LogWarning(lost, "Импорт {ImportId} отменён, но его файл {Path} убрать не удалось.", import.Id, import.BlobPath);
        }
    }

    private static string? Text(IFormCollection form, string key) =>
        form.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString().Trim() : null;

    private static Guid? FormGuid(IFormCollection form, string key) =>
        Text(form, key) is not { } text ? null
        : Guid.TryParse(text, out var id) ? id
        : throw new InvalidRequestException($"Поле «{key}» — не идентификатор: «{text}».");
}
