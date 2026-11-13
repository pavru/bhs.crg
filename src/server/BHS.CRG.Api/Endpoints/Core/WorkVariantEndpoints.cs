using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Варианты вида работ — дверь ядра (ТЗ WORK-49, CORE-8; change <c>work-06-work-type-variants</c>):
/// «в лотке», «в трубе» у «Прокладки кабеля». Записи типа «ВариантРаботы»
/// (<see cref="CoreRecordTypes.WorkVariant" />) пишутся только здесь, под правом классификатора
/// <c>core.worktypes.edit</c>, а общий адрес справочников их отвергает с адресом этой двери
/// (<see cref="OwnDoorTypes" />).
///
/// <para>Повторяет дверь видов работ (<see cref="WorkTypeEndpoints" />): те же адреса, права,
/// <c>If-Match</c> на каждом пишущем адресе, кроме заведения, и общий помощник
/// <see cref="OwnDoorWrites" />. Своё у неё — правило записи, которого общий путь не знает:</para>
/// <list type="bullet">
/// <item>вид работы — ССЫЛКОЙ на запись классификатора, а не вложенной копией: строка отчёта держит
/// запись вида, и копию нечем было бы удержать при переименовании;</item>
/// <item>вид при заведении существует и не в архиве (CORE-34.4): к архивному виду новый вариант не
/// заводится;</item>
/// <item>вид у заведённого варианта не меняется: «в лотке» прокладки кабеля, ставший «в лотке» прокладки
/// провода, молча переписал бы смысл каждой строки отчёта, которая на него ссылается;</item>
/// <item>наименование уникально среди живых вариантов ВИДА, а «есть в архиве» ищется в пределах вида.
/// Общая проверка ключа идентичности ссылку не видит (ключ строится из скаляров) и склеила бы
/// одноимённые варианты разных видов, поэтому команде дверь передаёт <c>createAnyway</c>.</item>
/// </list>
/// </summary>
public static class WorkVariantEndpoints
{
    /// <summary>Адрес двери — его же называет отказ общего адреса.</summary>
    public const string Door = "/api/work-variants";

    private const string WorkTypeKey = CoreRecordTypes.WorkVariantWorkTypeKey;
    private const string NameKey = CoreRecordTypes.WorkVariantNameKey;

    public static void MapWorkVariantEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogRead));
        var edit = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.WorkTypesEdit));

        // Список — с обязательным назначением (issue #1185): выбор варианта в строке отчёта архивных не
        // видит, страница справочника показывает их с признаком. Вид работы — необязательным отбором.
        g.MapGet("/", async (string? purpose, Guid? workTypeId, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
        {
            if (CommonDataEndpoints.Purpose(purpose) is not { } records) return CommonDataEndpoints.PurposeRequired();
            var all = await types.GetAllAsync(ct);
            // После права и назначения, как у видов работ: состояние справочника не открывается тому, кому
            // он не виден, а неправильный запрос остаётся неправильным и без справочника.
            if (Family(all) is not { } family) return DirectoryMissingResult(all);

            var entries = new List<DomainObject>();
            foreach (var typeId in family.Keys)
                entries.AddRange(await m.Send(new ListCommonDataEntriesQuery(records, CompositeTypeId: typeId), ct));
            return Results.Ok(entries
                .Where(e => workTypeId is null || WorkTypeOf(e) == workTypeId)
                .Select(e => WorkVariantEntryDto.Of(e, elide: true)));
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
            await VariantAsync(id, m, types, ct) is { } entry ? Results.Ok(WorkVariantEntryDto.Of(entry)) : Results.NotFound());

        edit.MapPost("/", async (CreateWorkVariantRequest req, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
        {
            var all = await types.GetAllAsync(ct);
            if (Family(all) is not { } family) return DirectoryMissingResult(all);
            // Тип — сам справочник либо его подтип: иначе право на классификатор заводило бы записи любого
            // справочника.
            var typeId = req.CompositeTypeId ?? family.Values.First(t => t.Code == CoreRecordTypes.WorkVariantCode).Id;
            if (!family.ContainsKey(typeId)) return Results.BadRequest(new { error = NotAVariant });
            if (RecordRequests.ParseData(req.Data) is not { } data) return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            if (await WorkTypeRefusalAsync(data.RootElement, all, m, ct) is { } badWorkType) return badWorkType;
            var workTypeId = CoreDirectories.Reference(data.RootElement, WorkTypeKey)!.Value;
            if (NameOf(data.RootElement) is not { } name) return NameRequired();
            if (await TwinAsync(family, workTypeId, name, except: null, m, ct) is { } twin)
            {
                if (!twin.IsArchived) return Duplicate(twin);
                // Тот же ответ, что у общего адреса (issue #1185): экран предлагает вернуть вариант кнопкой
                // или завести новый, подтвердив.
                if (req.CreateAnyway != true)
                    return RecordRequests.ArchivedTwin(new ArchivedTwinException(
                        twin.Id, twin.DisplayName ?? "", twin.ScopeLevel.ToString()));
            }

            var created = await m.Send(new CreateCommonDataEntryCommand(
                string.IsNullOrWhiteSpace(req.DisplayName) ? name : req.DisplayName, typeId, data,
                Scope(req.Scope), req.ScopeId, req.Aliases,
                // Свою проверку «есть в архиве» дверь уже сделала — в пределах вида; общая, по ключу без
                // ссылки, нашла бы одноимённый вариант чужого вида.
                CreateAnyway: true, req.RefsStandIn), ct);
            return Results.Ok(WorkVariantEntryDto.Of(created));
        });

        edit.MapPut("/{id:guid}", async (
            Guid id, UpdateWorkVariantRequest req, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            if (await VariantAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
            if (OwnDoorWrites.Seen(http, entry, Moved, out var seen) is { } refused) return refused;
            // Отсутствующее «data» — отказ, а не пустой объект: правка заменяет запись целиком, и «{}» стёрло
            // бы ссылку на вид и наименование, ответив 200 (как у видов работ).
            if (req.Data is null) return Results.BadRequest(new { error = DataRequired });
            if (RecordRequests.ParseData(req.Data) is not { } data) return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            if (Malformed(data.RootElement) is { } malformed) return malformed;
            // Вид у варианта постоянен. Архивность вида здесь не спрашивается: вариант архивного вида
            // остаётся правимым — переименовать его можно, перенести нельзя.
            if (CoreDirectories.Reference(data.RootElement, WorkTypeKey) != WorkTypeOf(entry))
                return Results.Conflict(new { error = MovedToOther, field = WorkTypeKey });
            if (NameOf(data.RootElement) is not { } name) return NameRequired();
            var family = Family(await types.GetAllAsync(ct))!;
            if (!entry.IsArchived && await TwinAsync(family, WorkTypeOf(entry)!.Value, name, except: id, m, ct) is { IsArchived: false } twin)
                return Duplicate(twin);

            var updated = await m.Send(new UpdateCommonDataEntryCommand(
                // Псевдонимы, о которых запрос молчит, остаются: null команда читает как «очистить».
                id, req.DisplayName, data, await access.ForAsync(user, ct), seen,
                req.Aliases ?? [.. entry.Aliases]), ct);
            return Results.Ok(WorkVariantEntryDto.Of(updated));
        });

        edit.MapDelete("/{id:guid}", async (
            Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types, ClaimsPrincipal user,
            IUserPermissions permissions, CancellationToken ct) =>
        {
            if (await VariantAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
            if (OwnDoorWrites.Seen(http, entry, Moved, out _) is { } refused) return refused;

            try { await m.Send(new DeleteCommonDataEntryCommand(id), ct); return Results.NoContent(); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Занятый вариант (CORE-34.1): тело отказа — то же, что у общего адреса, с числом держателей и
            // выходами «в архив» и «удалить, потеряв ссылки» (issue #1185, #1187).
            catch (ConflictException ex) { return await RecordRefusal.ConflictAsync(ex, id, m, user, permissions, ct); }
        });

        edit.MapPost("/{id:guid}/archive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, true, http, m, types, scopes, ct));
        edit.MapPost("/{id:guid}/unarchive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, false, http, m, types, scopes, ct));
    }

    /// <summary>
    /// Возврат из архива сверяет наименование: вернувшийся «в лотке» рядом с живым «в лотке» того же вида
    /// стал бы дублем, которого заведение не допускает.
    /// </summary>
    private static async Task<IResult> SetArchiveAsync(
        Guid id, bool archived, HttpRequest http, IMediator m, IRepository<DocumentType> types,
        IServiceScopeFactory scopes, CancellationToken ct)
    {
        if (await VariantAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
        if (OwnDoorWrites.Seen(http, entry, Moved, out _) is { } refused) return refused;
        if (!archived && WorkTypeOf(entry) is { } workTypeId && NameOf(entry.Data.RootElement) is { } name
            && await TwinAsync(Family(await types.GetAllAsync(ct))!, workTypeId, name, except: id, m, ct) is { IsArchived: false } twin)
            return Duplicate(twin);
        return await OwnDoorWrites.SetArchiveAsync(id, archived, m, scopes, ct);
    }

    /// <summary>
    /// Запись — если это вариант работы; иначе <c>null</c> и 404 у звавшего. Обязательна на КАЖДОМ адресе
    /// с идентификатором: без неё право на классификатор открывало бы правку любой записи общих данных.
    /// </summary>
    private static async Task<DomainObject?> VariantAsync(Guid id, IMediator m, IRepository<DocumentType> types, CancellationToken ct)
    {
        var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
        if (entry is null || entry.IsDocument) return null;
        return Family(await types.GetAllAsync(ct)) is { } family && family.ContainsKey(entry.CompositeTypeId) ? entry : null;
    }

    private static IReadOnlyDictionary<Guid, DocumentType>? Family(IReadOnlyList<DocumentType> all) =>
        CoreDirectories.Family(all, CoreRecordTypes.WorkVariantCode);

    /// <summary>Вид работы варианта — по ссылке в его данных; <c>null</c> — ссылки нет.</summary>
    internal static Guid? WorkTypeOf(DomainObject variant) => CoreDirectories.Reference(variant.Data.RootElement, WorkTypeKey);

    private static string? NameOf(JsonElement data) => CoreDirectories.Text(data, NameKey);

    /// <summary>
    /// Вид работы в данных заводимого варианта: ссылка, на запись классификатора, не в архиве. Отказ
    /// называет поле — по нему форма подсвечивает, что поправить.
    /// </summary>
    private static async Task<IResult?> WorkTypeRefusalAsync(
        JsonElement data, IReadOnlyList<DocumentType> all, IMediator m, CancellationToken ct)
    {
        if (Malformed(data) is { } malformed) return malformed;
        var id = CoreDirectories.Reference(data, WorkTypeKey)!.Value;
        var classifier = CoreDirectories.Family(all, CoreRecordTypes.WorkTypeCode);
        var workType = await m.Send(new GetCommonDataEntryQuery(id), ct);
        if (classifier is null || workType is null || workType.IsDocument || !classifier.ContainsKey(workType.CompositeTypeId))
            return Results.BadRequest(new { error = WorkTypeNotFound, field = WorkTypeKey });
        if (workType.IsArchived)
            return Results.Conflict(new { error = $"Вид работы «{workType.DisplayName}» в архиве: к нему вариант не заводится. " +
                                                  "Верните вид из архива или выберите другой.", field = WorkTypeKey });
        return null;
    }

    /// <summary>Поле вида работы — ссылка, а не пусто и не вложенная копия записи.</summary>
    private static IResult? Malformed(JsonElement data)
    {
        if (CoreDirectories.Reference(data, WorkTypeKey) is not null) return null;
        var inline = data.ValueKind == JsonValueKind.Object && data.TryGetProperty(WorkTypeKey, out var value)
                     && value.ValueKind == JsonValueKind.Object;
        return Results.BadRequest(new { error = inline ? InlineWorkType : WorkTypeRequired, field = WorkTypeKey });
    }

    /// <summary>
    /// Вариант того же вида с тем же наименованием (без учёта регистра и крайних пробелов): живой первым —
    /// он и есть дубль, архивный — «есть в архиве».
    /// </summary>
    private static async Task<DomainObject?> TwinAsync(
        IReadOnlyDictionary<Guid, DocumentType> family, Guid workTypeId, string name, Guid? except,
        IMediator m, CancellationToken ct)
    {
        var same = new List<DomainObject>();
        foreach (var typeId in family.Keys)
            same.AddRange((await m.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId), ct))
                .Where(e => e.Id != except && WorkTypeOf(e) == workTypeId
                            && string.Equals(NameOf(e.Data.RootElement), name, StringComparison.CurrentCultureIgnoreCase)));
        return same.OrderBy(e => e.IsArchived).FirstOrDefault();
    }

    private static IResult Duplicate(DomainObject twin) => Results.Conflict(new
    {
        error = $"У этого вида работы уже есть вариант «{NameOf(twin.Data.RootElement) ?? twin.DisplayName}». " +
                "Выберите его или назовите новый иначе.",
        field = NameKey, existingId = twin.Id,
    });

    private static IResult NameRequired() => Results.BadRequest(new { error = "Не заполнено наименование варианта.", field = NameKey });

    /// <summary>Уровень записи — как у видов работ: умолчание «Система», варианты — компании.</summary>
    private static CatalogScope Scope(string? scope) => scope switch
    {
        "Set" => CatalogScope.Set,
        "Section" => CatalogScope.Section,
        "Construction" => CatalogScope.Construction,
        _ => CatalogScope.System,
    };

    /// <summary>
    /// «Справочник не заведён» — ТЕМ ЖЕ телом, что у видов работ ([CORE-20.4], код
    /// <c>directory-missing</c>): нет классификатора — его причина (<c>unit-missing</c> /
    /// <c>not-created</c>) и его выход; классификатор есть, а вариантов нет — <c>not-created</c>, они
    /// появятся при следующем запуске.
    /// </summary>
    private static IResult DirectoryMissingResult(IReadOnlyList<DocumentType> all) =>
        CoreDirectories.Family(all, CoreRecordTypes.WorkTypeCode) is null
            ? WorkTypeEndpoints.DirectoryMissingResult(all)
            : Results.Conflict(new
            {
                code = EmployeeAccountReads.DirectoryMissing,
                reason = WorkTypeEndpoints.NotCreated,
                error = "Справочник вариантов вида работ ещё не заведён. Классификатор видов работ уже есть — " +
                        "справочник появится при следующем запуске приложения.",
            });

    private const string Moved =
        "Вариант тем временем изменили, и действие не выполнено. Откройте его заново и повторите.";

    private const string MovedToOther =
        "Вариант к другому виду работ не переносится: на него могут ссылаться строки отчёта о работах. " +
        "Заведите новый вариант у нужного вида.";

    private const string WorkTypeRequired = "Не указан вид работы, к которому относится вариант.";

    private const string InlineWorkType =
        "Вид работы указан копией записи, а не ссылкой на неё. Вариант держит запись классификатора — " +
        "выберите вид работы из справочника.";

    private const string WorkTypeNotFound =
        "Названный вид работы не найден в классификаторе видов работ.";

    private const string DataRequired =
        "В запросе нет поля «data» с реквизитами. Правка заменяет вариант целиком, поэтому пустое значение " +
        "стёрло бы ссылку на вид работы и наименование. Пришлите реквизиты, которые должны остаться.";

    private const string NotAVariant =
        "Названный тип — не вариант работы: этим адресом заводятся только записи справочника вариантов и его " +
        "подтипов. Записи других справочников заводятся своими адресами.";

    private record CreateWorkVariantRequest(
        string? DisplayName, string? Data, string[]? Aliases, bool? CreateAnyway = null, string? Scope = null,
        Guid? ScopeId = null, Guid? RefsStandIn = null, Guid? CompositeTypeId = null);

    private record UpdateWorkVariantRequest(string DisplayName, string? Data, string[]? Aliases);
}

/// <summary>Запись варианта с видом работы, к которому он относится, — полем, а не разбором ссылки в данных.</summary>
public sealed record WorkVariantEntryDto : CommonDataEntryDto
{
    public WorkVariantEntryDto(CommonDataEntryDto entry, Guid? workTypeId) : base(entry) => WorkTypeId = workTypeId;

    public Guid? WorkTypeId { get; init; }

    /// <param name="elide">Списочный ответ — без тяжёлых листьев (<see cref="RecordRequests.Elide" />).</param>
    public static WorkVariantEntryDto Of(DomainObject entry, bool elide = false)
    {
        var dto = CommonDataEntryDto.From(entry);
        return new(elide ? RecordRequests.Elide(dto) : dto, WorkVariantEndpoints.WorkTypeOf(entry));
    }
}
