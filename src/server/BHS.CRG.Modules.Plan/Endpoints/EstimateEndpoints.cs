using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Адреса смет и редакций (change <c>plan-06-estimate-model</c>, ТЗ PLAN-1, PLAN-2, PLAN-5, PLAN-5.2).
///
/// <para><b>Ворота.</b> Группа модуля закрыта воротами <c>module:plan</c>; права названы поимённо:
/// чтение — <c>plan.estimate.read</c>, правка — <c>plan.estimate.edit</c> (ТЗ AUTH-8, AUTH-9).
/// Доступ к модулю и право править сметы — разные вещи: у «Менеджера проекта» есть первое без
/// второго.</para>
///
/// <para><b>Версия.</b> Каждая правка существующей сметы или редакции называет версию, по которой
/// собрана, заголовком <c>If-Match</c>: без него — 400, с устаревшей — 409. Заголовок, а не поле тела:
/// у удаления и смены действующей тела нет. Строки пишет только импорт (<c>plan-07</c>) — через
/// <see cref="EstimateRevisionWriter" />.</para>
/// </summary>
public static class EstimateEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var estimates = endpoints.MapGroup("/api/plan/estimates").WithTags("Сметы");
        var read = AppPolicies.Permission(PlanModule.EstimateRead);
        var edit = AppPolicies.Permission(PlanModule.EstimateEdit);

        estimates.MapGet("/", ListAsync).RequireAuthorization(read);
        estimates.MapGet("/{id:guid}", GetAsync).RequireAuthorization(read);
        estimates.MapPost("/", CreateAsync).RequireAuthorization(edit);
        estimates.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(edit);
        estimates.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(edit);
        estimates.MapPost("/{id:guid}/current-revision", MakeCurrentAsync).RequireAuthorization(edit);
        estimates.MapGet("/{id:guid}/current-revision/preview", CurrentRevisionPreview.GetAsync).RequireAuthorization(edit);

        endpoints.MapDelete("/api/plan/revisions/{id:guid}", DeleteRevisionAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
    }

    /// <summary>
    /// Сметы стройки — все сразу; без стройки — сметы всех строек страницами (D6 <c>plan-31</c>, ТЗ PLAN-20): у
    /// каждой стройка и раздел, а название стройки клиент берёт справочником строек. Курсор и размер страницы у
    /// списка стройки — 400, а не молчаливое «всё».
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid? constructionId, string? cursor, int? limit, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        var hidden = await EstimateViews.HiddenAsync(user, ct);
        if (constructionId is { } site)
        {
            // Молча отдать всё на «limit=20» — отказ, переодетый в результат: клиент счёл бы ответ одной страницей.
            if (cursor is not null || limit is not null)
                throw new InvalidRequestException(
                    "Сметы стройки приходят целиком: курсор («cursor») и размер страницы («limit») — только у общего " +
                    "списка, без «constructionId».");
            var items = await EstimateViews.ListAsync(db, db.Estimates.Where(e => e.ConstructionId == site), hidden is null, ct);
            return Results.Ok(new EstimateListDto(items, null, hidden));
        }

        var (page, next) = await EstimateViews.PageAsync(db, cursor, limit, hidden is null, ct);
        return Results.Ok(new EstimateListDto(page, next, hidden));
    }

    private static async Task<IResult> GetAsync(Guid id, PlanDbContext db, IModuleUser user, CancellationToken ct) =>
        Results.Ok(await EstimateViews.OneAsync(db, user, id, ct));

    private static async Task<IResult> CreateAsync(
        EstimateCreateRequest body, PlanDbContext db, IModuleUser user, IModuleConstructions constructions,
        IModuleActivityLog log, CancellationToken ct)
    {
        // Стройка и раздел — новые ссылки (ТЗ CORE-34.4): обязаны быть на месте, раздел — этой стройки.
        // Архива у строек и разделов нет, поэтому вопрос один — есть ли.
        var site = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == body.ConstructionId)
            ?? throw new InvalidRequestException("Стройки нет — её удалили либо адрес неверен.");
        if (body.SectionId is { } section && site.Sections.All(s => s.Id != section))
            throw new InvalidRequestException($"Раздела нет у стройки «{site.Name}» — смета на раздел составляется в его стройке.");

        var estimate = Estimate.Create(site.Id, body.SectionId, body.Name ?? string.Empty, Kind(body.Kind), DateTimeOffset.UtcNow);
        db.Estimates.Add(estimate);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(EstimateActions.Created, estimate.Id.ToString(), EstimateActions.Label(estimate),
            after: $"стройка «{site.Name}»", ct: ct);
        return Results.Created($"/api/plan/estimates/{estimate.Id}", await EstimateViews.OneAsync(db, user, estimate.Id, ct));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, EstimateUpdateRequest body, HttpRequest request, PlanDbContext db, IModuleUser user,
        IModuleActivityLog log, CancellationToken ct)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw EstimateViews.NotFound();
        EnsureSeen(request, db.VersionOf(estimate), "Смету");

        var before = EstimateActions.Label(estimate);
        estimate.Rename(body.Name ?? string.Empty, string.IsNullOrWhiteSpace(body.Kind) ? null : Kind(body.Kind));
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(EstimateActions.Changed, estimate.Id.ToString(), EstimateActions.Label(estimate),
            before, EstimateActions.Label(estimate), ct);
        return Results.Ok(await EstimateViews.OneAsync(db, user, id, ct));
    }

    /// <summary>
    /// Удаляется только смета без редакций: у неё нет ничего, из чего сделаны КП и график. Несостоявшиеся
    /// импорты в неё уходят вместе с ней (<c>plan-07</c>) — и их файлы тоже.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, HttpRequest request, PlanDbContext db, IModuleActivityLog log,
        [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs, ILoggerFactory logs, CancellationToken ct)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw EstimateViews.NotFound();
        EnsureSeen(request, db.VersionOf(estimate), "Смету");

        if (await db.EstimateRevisions.AnyAsync(r => r.EstimateId == id, ct))
            throw new ConflictException("Смету нельзя удалить: у неё есть редакции. Удаляется только смета без редакций.");

        // Импорт, заведённый между этим чтением и сохранением, уйдёт каскадом без своего файла: файл без ссылки
        // найдёт уборка осиротевших (OrphanBlobCleanup) — как и после любого сбоя между записью и хранилищем.
        var imports = await db.EstimateImports.AsNoTracking().Where(i => i.EstimateId == id).ToListAsync(ct);
        db.Estimates.Remove(estimate);
        await db.SaveChangesAsync(ct);
        foreach (var import in imports)
            await EstimateImportEndpoints.DeleteFileAsync(blobs, import, logs.CreateLogger(typeof(EstimateEndpoints)));
        await log.RecordAsync(EstimateActions.Deleted, id.ToString(), EstimateActions.Label(estimate), ct: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Сделать редакцию действующей (ТЗ PLAN-5.2, решение В-5): с ней меняется план стройки. Версия —
    /// сметы: действующую держит она, и две вкладки, выбирающие разное, не должны молча перебить друг друга.
    /// </summary>
    private static async Task<IResult> MakeCurrentAsync(
        Guid id, CurrentRevisionRequest body, HttpRequest request, PlanDbContext db, IModuleUser user,
        IModuleActivityLog log, CancellationToken ct)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw EstimateViews.NotFound();
        EnsureSeen(request, db.VersionOf(estimate), "Смету");

        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == body.RevisionId, ct)
            ?? throw new InvalidRequestException("Редакции нет — её удалили либо она не этой сметы.");
        if (estimate.CurrentRevisionId == revision.Id) return Results.Ok(await EstimateViews.OneAsync(db, user, id, ct));

        var was = estimate.CurrentRevisionId is { } previous
            ? (await db.EstimateRevisions.Where(r => r.Id == previous).Select(r => (int?)r.Number).FirstOrDefaultAsync(ct))
            : null;
        estimate.MakeCurrent(revision);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(EstimateActions.CurrentRevision, estimate.Id.ToString(), EstimateActions.Label(estimate),
            was is { } number ? $"редакция {number}" : "нет", $"редакция {revision.Number}", ct);
        return Results.Ok(await EstimateViews.OneAsync(db, user, id, ct));
    }

    /// <summary>
    /// Удалить редакцию: не зафиксированную (из неё ничего не сделано, ТЗ PLAN-5) и не действующую (по
    /// ней считается план, ТЗ PLAN-5.2). Версия — самой редакции.
    /// </summary>
    private static async Task<IResult> DeleteRevisionAsync(
        Guid id, HttpRequest request, PlanDbContext db, IModuleActivityLog log, CancellationToken ct)
    {
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        EnsureSeen(request, db.VersionOf(revision), "Редакцию");

        revision.EnsureEditable("Удалить редакцию");
        var estimate = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
        if (estimate.CurrentRevisionId == revision.Id)
            throw new ConflictException(
                $"Редакцию {revision.Number} нельзя удалить: она действующая — по ней считается план стройки. " +
                "Сначала сделайте действующей другую редакцию.");

        db.EstimateRevisions.Remove(revision);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(EstimateActions.RevisionDeleted, revision.Id.ToString(), EstimateActions.Label(estimate),
            before: $"редакция {revision.Number}", ct: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Версия, по которой собрана правка, — заголовком <c>If-Match</c>, как у счёта и накладной.
    /// </summary>
    /// <param name="what">Что изменили, в винительном с заглавной: «Смету», «Редакцию».</param>
    /// <param name="reread">Что перечитать, в винительном: где человек увидит новое состояние.</param>
    internal static void EnsureSeen(HttpRequest request, string current, string what, string reread = "смету")
    {
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException(
                $"Не названа версия, по которой собрана правка (заголовок {SeenVersion.Header}) — она приходит в " +
                "ответе чтения полем «version». Без неё правка записалась бы поверх чужой.");
        if (seen != current)
            throw new ConflictException(
                $"{what} тем временем изменили, и это действие не выполнено. Перечитайте {reread} и повторите: вы " +
                "видели прежнее состояние.");
    }

    internal static EstimateKind Kind(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        null or "" or "main" => EstimateKind.Main,
        "additional" => EstimateKind.Additional,
        _ => throw new InvalidRequestException($"Вид сметы «{code}» неизвестен: ожидается main или additional."),
    };
}
