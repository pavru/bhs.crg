using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal static class PurchaseRequestEndpoints
{
    private const string Path = "/api/costs/requests";
    private const int PageSize = 100;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).WithTags("Заявки на закупку");
        var read = AppPolicies.Permission(CostsModule.RequestRead);
        var edit = AppPolicies.Permission(CostsModule.RequestEdit);
        group.MapGet("/", ListAsync).RequireAuthorization(read);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(read);
        group.MapPost("/draft-from-estimate", SuggestAsync).RequireAuthorization(edit);
        group.MapPost("/", CreateAsync).RequireAuthorization(edit);
        group.MapPut("/{id:guid}/lines", ReplaceLinesAsync).RequireAuthorization(edit);
        group.MapPost("/{id:guid}/submit", SubmitAsync).RequireAuthorization(edit);
        group.MapPost("/{id:guid}/return", ReturnAsync).RequireAuthorization(edit);
        group.MapPost("/{id:guid}/close", CloseAsync).RequireAuthorization(edit);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(edit);
    }

    private static async Task<IResult> ListAsync(
        CostsDbContext db, Guid? constructionId, long? beforeNumber, CancellationToken ct)
    {
        var query = db.PurchaseRequests.AsNoTracking();
        if (constructionId is { } site) query = query.Where(request => request.ConstructionId == site);
        if (beforeNumber is { } before) query = query.Where(request => request.Number < before);

        var page = await query.OrderByDescending(request => request.Number).Take(PageSize + 1)
            .Select(request => new
            {
                Request = request,
                Version = EF.Property<uint>(request, CostsDbContext.RowVersion),
                LineCount = request.Lines.Count,
            }).ToListAsync(ct);
        var items = page.Take(PageSize).Select(row => new
        {
            row.Request.Id,
            row.Request.Number,
            row.Request.ConstructionId,
            row.Request.CreatedBy,
            row.Request.CreatedAt,
            row.Request.UpdatedAt,
            Status = Code(row.Request.Status),
            Version = VersionText(row.Version),
            row.LineCount,
        }).ToArray();
        return Results.Ok(new { items, nextBeforeNumber = page.Count > PageSize ? items[^1].Number : (long?)null });
    }

    private static async Task<IResult> GetAsync(Guid id, CostsDbContext db, CancellationToken ct)
    {
        var request = await FindAsync(db, id, ct);
        return Results.Ok(View(db, request));
    }

    private static async Task<IResult> SuggestAsync(
        PurchaseRequestSource source, PurchaseRequestEstimate estimate, CancellationToken ct)
    {
        var answer = await estimate.ReadAsync(source, ct);
        if (Hidden(answer) is { } hidden)
            return Results.Ok(new { state = "hidden", permission = hidden.Permission, message = hidden.Text });

        var (plan, materials) = Shown(answer);
        var suggestion = await estimate.SuggestAsync(source, plan, materials, ct);
        return Results.Ok(new { state = "shown", suggestion });
    }

    private static async Task<IResult> CreateAsync(
        PurchaseRequestCreate body, PurchaseRequestEstimate estimate, PurchaseRequestLines lines,
        CostsDbContext db, IModuleUser user, CancellationToken ct)
    {
        var source = new PurchaseRequestSource(body.ConstructionId, body.SectionId, body.PlanItemIds);
        var answer = await estimate.ReadAsync(source, ct);
        if (Hidden(answer) is { } hidden) throw new ForbiddenException(hidden.Text);
        var (_, materials) = Shown(answer);
        if (materials.Lines.Count == 0)
            throw new ConflictException("В выбранной части сметы нет материалов с позицией номенклатуры.");

        var validated = await lines.ValidateAsync(body.Lines, body.ConstructionId, [], ct);
        var now = DateTimeOffset.UtcNow;
        var request = PurchaseRequest.Create(body.ConstructionId, UserId(user), now);
        request.ReplaceLines(validated, now);
        db.PurchaseRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return Results.Created($"{Path}/{request.Id}", View(db, request));
    }

    private static async Task<IResult> ReplaceLinesAsync(
        Guid id, PurchaseRequestEdit body, HttpRequest http, CostsDbContext db, PurchaseRequestLines lines,
        CancellationToken ct)
    {
        var request = await ForWriteAsync(id, http, db, ct);
        RequireStatus(request, PurchaseRequestStatus.Draft, "Строки можно править только в черновике. Поданную заявку сначала верните в черновик.");
        var validated = await lines.ValidateAsync(body.Lines, request.ConstructionId, request.Lines, ct);
        request.ReplaceLines(validated, DateTimeOffset.UtcNow);
        // Версия шапки защищает весь набор строк, даже если часы вернули то же время.
        db.Entry(request).Property(row => row.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(db, request));
    }

    private static Task<IResult> SubmitAsync(Guid id, HttpRequest http, CostsDbContext db, CancellationToken ct) =>
        ChangeStatusAsync(id, http, db, PurchaseRequestStatus.Draft, PurchaseRequestStatus.Submitted,
            "Подать можно только черновик заявки.", ct);

    private static Task<IResult> CloseAsync(Guid id, HttpRequest http, CostsDbContext db, CancellationToken ct) =>
        ChangeStatusAsync(id, http, db, PurchaseRequestStatus.Submitted, PurchaseRequestStatus.Closed,
            "Закрыть можно только поданную заявку.", ct);

    private static async Task<IResult> ReturnAsync(
        Guid id, PurchaseRequestReturn body, HttpRequest http, CostsDbContext db, IModuleUser user,
        PurchaseRequestReturnJournal journal, CancellationToken ct)
    {
        var request = await ForWriteAsync(id, http, db, ct);
        RequireStatus(request, PurchaseRequestStatus.Submitted, "Вернуть в черновик можно только поданную заявку.");
        var authorId = UserId(user);
        if (request.CreatedBy != authorId)
            throw new ForbiddenException("Вернуть заявку в черновик может только её автор.");
        var reason = body.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > PurchaseRequestLine.NoteLength)
            throw new InvalidRequestException("Укажите причину возврата длиной от 1 до 2000 знаков.");

        var now = DateTimeOffset.UtcNow;
        request.ChangeStatus(PurchaseRequestStatus.Draft, now);
        var returned = PurchaseRequestReturnEvent.Create(request, authorId, user.Name, now, reason);
        db.PurchaseRequestReturnEvents.Add(returned);
        await db.SaveChangesAsync(ct);
        await journal.DeliverAsync(returned, ct);
        return Results.Ok(View(db, request));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpRequest http, CostsDbContext db, CancellationToken ct)
    {
        var request = await ForWriteAsync(id, http, db, ct);
        RequireStatus(request, PurchaseRequestStatus.Draft, "Удалить можно только черновик заявки.");
        db.PurchaseRequests.Remove(request);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid id, HttpRequest http, CostsDbContext db, PurchaseRequestStatus from, PurchaseRequestStatus to,
        string refusal, CancellationToken ct)
    {
        var request = await ForWriteAsync(id, http, db, ct);
        RequireStatus(request, from, refusal);
        request.ChangeStatus(to, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(db, request));
    }

    private static async Task<PurchaseRequest> ForWriteAsync(
        Guid id, HttpRequest http, CostsDbContext db, CancellationToken ct)
    {
        if (!SeenVersion.TryRead(http, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException("Укажите в If-Match версию заявки из поля version ответа чтения.");

        var request = await FindAsync(db, id, ct);
        if (seen != db.VersionOf(request))
            throw new ConflictException("Заявку тем временем изменили. Перечитайте её и повторите правку.");
        return request;
    }

    private static async Task<PurchaseRequest> FindAsync(CostsDbContext db, Guid id, CancellationToken ct) =>
        await db.PurchaseRequests.Include(request => request.Lines).AsSingleQuery()
            .SingleOrDefaultAsync(request => request.Id == id, ct)
        ?? throw new NotFoundException("Заявка не найдена.");

    private static void RequireStatus(PurchaseRequest request, PurchaseRequestStatus status, string refusal)
    {
        if (request.Status != status) throw new ConflictException(refusal);
    }

    private static Guid UserId(IModuleUser user) =>
        user.Id ?? throw new ForbiddenException("Для заявки нужна учётная запись пользователя.");

    private static ContractAnswer<PlannedMaterials>.Hidden? Hidden(ContractAnswer<PlannedWorksAnswer> answer) =>
        (answer as ContractAnswer<PlannedWorksAnswer>.Shown)?.Value.Materials as ContractAnswer<PlannedMaterials>.Hidden;

    private static (PlannedWorksAnswer Plan, PlannedMaterials Materials) Shown(ContractAnswer<PlannedWorksAnswer> answer) =>
        answer is ContractAnswer<PlannedWorksAnswer>.Shown { Value.Materials: ContractAnswer<PlannedMaterials>.Shown materials } plan
            ? (plan.Value, materials.Value)
            : throw new ForbiddenException("Материалы сметы доступны только от имени пользователя с правом чтения.");

    private static string Code(PurchaseRequestStatus status) => status.ToString().ToLowerInvariant();
    private static string VersionText(uint version) => version.ToString(CultureInfo.InvariantCulture);
    private static object View(CostsDbContext db, PurchaseRequest request) => new
    {
        request.Id,
        request.Number,
        request.ConstructionId,
        request.CreatedBy,
        request.CreatedAt,
        request.UpdatedAt,
        Status = Code(request.Status),
        Version = db.VersionOf(request),
        Lines = request.Lines.OrderBy(line => line.Position).Select(line => new
        {
            line.NomenclatureId, line.PlanItemId, line.Quantity, line.Unit, line.Note,
        }).ToArray(),
    };
}
