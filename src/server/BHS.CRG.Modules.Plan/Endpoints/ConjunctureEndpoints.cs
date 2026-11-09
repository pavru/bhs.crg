using System.Net.Mime;
using BHS.CRG.Domain.Common;
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
/// Адреса приложений к смете (change <c>plan-11-conjuncture</c>, ТЗ PLAN-9, PLAN-7, решение В-7 (а)): конъюнктурный
/// анализ и ведомость объёмов работ — загрузка тем же разбором, что ЛСР, принятие отчёта, чтение, файл, удаление.
///
/// <para><b>Загрузка и принятие</b> — общие для анализа и ведомости: вид называет сам файл (разбор по содержимому,
/// как у импорта ЛСР). Загрузка заводит импорт в эту смету, принятие (<c>…/conjuncture/{importId}/accept</c>)
/// заводит приложение. Принятие того же импорта из окна загрузки сметы — тот же адрес.</para>
///
/// <para><b>Ворота.</b> Чтение — <c>plan.estimate.read</c>, цены анализа — только с <c>plan.estimate.cost</c> (D4);
/// загрузка, принятие и удаление — <c>plan.estimate.edit</c>. Удаление называет версию заголовком <c>If-Match</c>;
/// заведение — нет: называть нечего (design).</para>
/// </summary>
public static class ConjunctureEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var read = AppPolicies.Permission(PlanModule.EstimateRead);
        var edit = AppPolicies.Permission(PlanModule.EstimateEdit);

        var estimates = endpoints.MapGroup("/api/plan/estimates").WithTags("Приложения к смете");
        estimates.MapGet("/{id:guid}/conjuncture", ListConjunctureAsync).RequireAuthorization(read);
        estimates.MapGet("/{id:guid}/volume-statements", ListVolumeAsync).RequireAuthorization(read);
        estimates.MapPost("/{id:guid}/conjuncture", UploadAsync).RequireAuthorization(edit).DisableAntiforgery();
        estimates.MapPost("/{id:guid}/conjuncture/{importId:guid}/accept", AcceptAsync).RequireAuthorization(edit);

        var conjuncture = endpoints.MapGroup("/api/plan/conjuncture").WithTags("Приложения к смете");
        conjuncture.MapGet("/{id:guid}/items", ItemsAsync).RequireAuthorization(read);
        conjuncture.MapGet("/{id:guid}/file", ConjunctureFileAsync).RequireAuthorization(read);
        conjuncture.MapDelete("/{id:guid}", DeleteConjunctureAsync).RequireAuthorization(edit);

        var volume = endpoints.MapGroup("/api/plan/volume-statements").WithTags("Приложения к смете");
        volume.MapGet("/{id:guid}/lines", LinesAsync).RequireAuthorization(read);
        volume.MapGet("/{id:guid}/file", VolumeFileAsync).RequireAuthorization(read);
        volume.MapDelete("/{id:guid}", DeleteVolumeAsync).RequireAuthorization(edit);
    }

    private static async Task<IResult> ListConjunctureAsync(Guid id, PlanDbContext db, CancellationToken ct)
    {
        await EnsureEstimateAsync(db, id, ct);
        return Results.Ok(await ConjunctureViews.ListAsync(db, db.ConjunctureAnalyses.Where(a => a.EstimateId == id), ct));
    }

    private static async Task<IResult> ListVolumeAsync(Guid id, PlanDbContext db, CancellationToken ct)
    {
        await EnsureEstimateAsync(db, id, ct);
        return Results.Ok(await ConjunctureViews.ListAsync(db, db.VolumeStatements.Where(s => s.EstimateId == id), ct));
    }

    /// <summary>
    /// Загрузить файл приложения: форма <c>file</c>. Ответ — импорт в эту смету в состоянии <c>parsing</c> (отчёт
    /// <c>plan-07</c>); приложение заводит принятие отчёта.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        Guid id, IFormFile file, PlanDbContext db, IModuleUser user, IModuleJobs jobs,
        [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs, ILoggerFactory logs, CancellationToken ct)
    {
        EstimateImportEndpoints.EnsureFile(file);
        var estimate = await db.Estimates.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw EstimateViews.NotFound();
        var import = await EstimateImportEndpoints.StartAsync(file, db, jobs, blobs, logs,
            (path, now) => EstimateImport.IntoEstimate(estimate, path, file.FileName, user.Id, user.Name, now), ct);
        return Results.Created($"/api/plan/imports/{import.Id}",
            await EstimateImportViews.OneAsync(db, user, jobs, import.Id, CancellationToken.None));
    }

    /// <summary>
    /// Принять отчёт разбора — завести приложение (<see cref="EstimateAttach" />). Не разобранные без согласия — 409
    /// с числами полями ответа; повтор после принятия — 200 с заведённым, без второго.
    /// </summary>
    private static async Task<IResult> AcceptAsync(
        Guid id, Guid importId, EstimateAttachRequest? body, PlanDbContext db, EstimateAttach attach, CancellationToken ct)
    {
        EstimateAttached done;
        try
        {
            done = await attach.RunAsync(id, importId, body?.AcceptPartial ?? false, ct);
        }
        catch (PartialAttachmentException partial)
        {
            return Results.Conflict(new
            {
                error = partial.Message, code = "partial",
                parsed = partial.Parsed, inFile = partial.InFile, unparsed = partial.Unparsed,
            });
        }

        var dto = new EstimateAttachedDto(await ConjunctureViews.OneAsync(db, done.Attachment, ct), done.Repeated);
        return done.Repeated ? Results.Ok(dto) : Results.Created(ConjunctureViews.Location(done.Attachment), dto);
    }

    private static async Task<IResult> ItemsAsync(
        Guid id, int? offset, int? limit, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        if (!await db.ConjunctureAnalyses.AnyAsync(a => a.Id == id, ct)) throw ConjunctureViews.NotFound("ka");
        return Results.Ok(await ConjunctureViews.ItemsAsync(db, user, id, offset, limit, ct));
    }

    private static async Task<IResult> LinesAsync(Guid id, int? offset, int? limit, PlanDbContext db, CancellationToken ct)
    {
        if (!await db.VolumeStatements.AnyAsync(s => s.Id == id, ct)) throw ConjunctureViews.NotFound("vor");
        return Results.Ok(await ConjunctureViews.LinesAsync(db, id, offset, limit, ct));
    }

    /// <summary>
    /// Файл анализа несёт все цены предложений — его отдаёт только право на стоимость (D4): иначе скачивание обходило бы
    /// то, что скрывает чтение позиций. У ВОР денег нет, её файл — под чтением.
    /// </summary>
    private static async Task<IResult> ConjunctureFileAsync(
        Guid id, PlanDbContext db, IModuleUser user, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs,
        CancellationToken ct)
    {
        var analysis = await db.ConjunctureAnalyses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ConjunctureViews.NotFound("ka");
        if (!await user.HasAsync(PlanModule.EstimateCost, ct))
            throw new ForbiddenException(
                $"В файле конъюнктурного анализа — цены предложений, а их показывает только право «{PlanModule.EstimateCost}» ({PlanModule.EstimateCostGives}).");
        return await FileAsync(analysis, blobs, ct);
    }

    private static async Task<IResult> VolumeFileAsync(
        Guid id, PlanDbContext db, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs, CancellationToken ct) =>
        await FileAsync(await db.VolumeStatements.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ConjunctureViews.NotFound("vor"), blobs, ct);

    /// <summary>Файл источника — как его загрузили: спор «что было в файле» решается им, а не разобранным.</summary>
    private static async Task<IResult> FileAsync(EstimateAttachment attachment, IModuleBlobs blobs, CancellationToken ct) =>
        Results.File(await blobs.OpenAsync(attachment.BlobPath, ct), MediaTypeNames.Application.Octet, attachment.FileName);

    private static async Task<IResult> DeleteConjunctureAsync(
        Guid id, HttpRequest request, PlanDbContext db, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs,
        ILoggerFactory logs, CancellationToken ct) =>
        await DeleteAsync(await db.ConjunctureAnalyses.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ConjunctureViews.NotFound("ka"), request, db, blobs, logs, ct);

    private static async Task<IResult> DeleteVolumeAsync(
        Guid id, HttpRequest request, PlanDbContext db, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs,
        ILoggerFactory logs, CancellationToken ct) =>
        await DeleteAsync(await db.VolumeStatements.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ConjunctureViews.NotFound("vor"), request, db, blobs, logs, ct);

    /// <summary>
    /// Удалить приложение с его позициями (каскад), потом — файл. Сначала запись: упавшее между ними оставит файл без
    /// ссылки — его найдёт уборка осиротевших, а запись без файла обещала бы файл, которого нет.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        EstimateAttachment attachment, HttpRequest request, PlanDbContext db, IModuleBlobs blobs, ILoggerFactory logs,
        CancellationToken ct)
    {
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(attachment), "Приложение к смете", "приложения сметы");
        db.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await EstimateImportEndpoints.DeleteFileAsync(blobs, attachment.BlobPath, $"Приложение {attachment.Id}",
            logs.CreateLogger(typeof(ConjunctureEndpoints)));
        return Results.NoContent();
    }

    private static async Task EnsureEstimateAsync(PlanDbContext db, Guid id, CancellationToken ct)
    {
        if (!await db.Estimates.AnyAsync(e => e.Id == id, ct)) throw EstimateViews.NotFound();
    }
}
