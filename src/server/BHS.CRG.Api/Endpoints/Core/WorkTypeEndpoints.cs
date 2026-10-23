using System.Security.Claims;
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
/// Классификатор видов работ — дверь ядра (ТЗ CORE-8, решение владельца WORK-59 от 22.10.2026).
///
/// <para><b>Своя дверь ради ПРАВА.</b> Классификатор ведёт сметчик: сопоставляя строки сметы с видами
/// работ, он заводит недостающий вид прямо из окна сопоставления (PLAN-6.1). Общих данных он не правит
/// — <c>core.catalog.edit</c> у него нет, — и на общем адресе право <c>core.worktypes.edit</c> было бы
/// галкой, которая ничего не открывает (до этой двери оно ею и было). Поэтому записи типа «ВидРаботы»
/// пишутся только здесь, а общий адрес их отвергает с адресом этой двери
/// (<see cref="OwnDoorTypes.RefuseCommonPathAsync" />).</para>
///
/// <para>Дверь тонкая, как у сотрудников (<see cref="EmployeeEndpoints" />): вид работы — запись общих
/// данных, и зовутся те же команды. Чтение — под <c>core.catalog.read</c>: классификатор читают все,
/// кто видит справочники.</para>
///
/// <para><b>Версия — на КАЖДОМ пишущем адресе</b>, кроме заведения (у новой записи версии ещё нет), —
/// включая удаление и архив. Общий адрес архивирует без версии; новая дверь этого послабления не
/// наследует (ТЗ AUTH-9). Удаление и архив сверяют версию ДО команды: у этих команд параметра версии
/// нет, и окно между сверкой и записью остаётся — правка содержимого в него не попадает (архив и
/// удаление содержимого не пишут), а правка, успевшая раньше, сверку не проходит.</para>
/// </summary>
public static class WorkTypeEndpoints
{
    /// <summary>Адрес двери — его же называет отказ общего адреса.</summary>
    public const string Door = "/api/work-types";

    public static void MapWorkTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogRead));
        var edit = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.WorkTypesEdit));

        // Список — с обязательным назначением, как у общего адреса (issue #1185): классификатор кормит
        // и выбор вида работы (архивных нет), и страницу справочника (архивные на месте).
        g.MapGet("/", async (string? purpose, string? q, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
        {
            if (CommonDataEndpoints.Purpose(purpose) is not { } records) return CommonDataEndpoints.PurposeRequired();
            var all = await types.GetAllAsync(ct);
            if (CoreDirectories.Family(all, CoreRecordTypes.WorkTypeCode) is not { } family)
                return Results.Ok(Array.Empty<CommonDataEntryDto>());

            // Ключи — по типу, а не по записи, и только под поиск: схема разбирается раз на тип.
            var units = CoreDirectories.Family(all, CoreRecordTypes.UnitCode)?.Keys.ToHashSet() ?? [];
            var entries = new List<DomainObject>();
            foreach (var typeId in family.Keys)
            {
                var found = await m.Send(new ListCommonDataEntriesQuery(records, CompositeTypeId: typeId), ct);
                if (string.IsNullOrWhiteSpace(q)) { entries.AddRange(found); continue; }
                var keys = WorkTypeKeys.Of(family[typeId], all, units);
                entries.AddRange(found.Where(e => Matches(e, q.Trim(), keys)));
            }
            return Results.Ok(entries.Select(CommonDataEntryDto.From).Select(RecordRequests.Elide));
        });

        // По идентификатору — запись ЦЕЛИКОМ: этот путь кормит редактор (issue #520).
        g.MapGet("/{id:guid}", async (Guid id, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
            await WorkTypeAsync(id, m, types, ct) is { } entry ? Results.Ok(CommonDataEntryDto.From(entry)) : Results.NotFound());

        edit.MapPost("/", async (CreateWorkTypeRequest req, IMediator m, IRepository<DocumentType> types, CancellationToken ct) =>
        {
            if (CoreDirectories.Family(await types.GetAllAsync(ct), CoreRecordTypes.WorkTypeCode) is not { } family)
                return Results.Conflict(new { error = DirectoryMissing });
            // Тип — сам классификатор либо его подтип; иной тип дверь не заводит, иначе право на
            // классификатор открывало бы заведение записей любого справочника.
            var typeId = req.CompositeTypeId ?? family.Values.First(t => t.Code == CoreRecordTypes.WorkTypeCode).Id;
            if (!family.ContainsKey(typeId)) return Results.BadRequest(new { error = NotAWorkType });
            if (RecordRequests.ParseData(req.Data) is not { } data) return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            try
            {
                return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand(
                    req.DisplayName, typeId, data, Scope(req.Scope), req.ScopeId, req.Aliases,
                    req.CreateAnyway ?? false, req.RefsStandIn), ct)));
            }
            // Тот же ответ, что у общего адреса (issue #1185): вид с тем же кодом лежит в архиве, и экран
            // предлагает вернуть его кнопкой или завести, подтвердив.
            catch (ArchivedTwinException ex) { return RecordRequests.ArchivedTwin(ex); }
        });

        edit.MapPut("/{id:guid}", async (
            Guid id, UpdateWorkTypeRequest req, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            if (await WorkTypeAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
            if (Seen(http, entry, out var seen) is { } refused) return refused;
            // Отсутствующее «data» — отказ, а не пустой объект: правка заменяет запись целиком, и
            // подстановка «{}» стёрла бы код, единицу и признаки, ответив 200 (как у сотрудников).
            if (req.Data is null) return Results.BadRequest(new { error = DataRequired });
            if (RecordRequests.ParseData(req.Data) is not { } data) return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand(
                // Псевдонимы, о которых запрос молчит, остаются: null команда читает как «очистить».
                id, req.DisplayName, data, await access.ForAsync(user, ct), seen,
                req.Aliases ?? [.. entry.Aliases]), ct)));
        });

        edit.MapDelete("/{id:guid}", async (
            Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types, ClaimsPrincipal user,
            IUserPermissions permissions, CancellationToken ct) =>
        {
            if (await WorkTypeAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
            if (Seen(http, entry, out _) is { } refused) return refused;

            try { await m.Send(new DeleteCommonDataEntryCommand(id), ct); return Results.NoContent(); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Тело отказа — то же, что у общего адреса: выходы «в архив» и «удалить, потеряв ссылки»
            // у вида работы те же (issue #1185, #1187).
            catch (ConflictException ex) { return await RecordRefusal.ConflictAsync(ex, id, m, user, permissions, ct); }
        });

        edit.MapPost("/{id:guid}/archive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, true, http, m, types, scopes, ct));
        edit.MapPost("/{id:guid}/unarchive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, false, http, m, types, scopes, ct));
    }

    /// <summary>
    /// Ответ архива несёт НОВУЮ версию: архив её сдвигает, и экран, предлагающий «Вернуть» тостом,
    /// без неё назвал бы прежнюю — и получил бы отказ на собственное действие.
    ///
    /// <para>⚠️ Версия читается в СВЕЖЕЙ области. Архив пишет признак условным обновлением мимо
    /// отслеживания, а запись, прочитанная выше для сверки, осталась в контексте запроса — повторное
    /// чтение там отдало бы её же, с прежней версией (поймано тестом).</para>
    /// </summary>
    private static async Task<IResult> SetArchiveAsync(
        Guid id, bool archived, HttpRequest http, IMediator m, IRepository<DocumentType> types,
        IServiceScopeFactory scopes, CancellationToken ct)
    {
        if (await WorkTypeAsync(id, m, types, ct) is not { } entry) return Results.NotFound();
        if (Seen(http, entry, out _) is { } refused) return refused;

        try
        {
            var done = await m.Send(new SetRecordArchiveCommand(id, archived), ct);
            using var fresh = scopes.CreateScope();
            var after = await fresh.ServiceProvider.GetRequiredService<IMediator>().Send(new GetCommonDataEntryQuery(id), ct);
            return Results.Ok(new { done.Id, done.DisplayName, done.Archived, done.Changed, version = after?.Version });
        }
        catch (NotFoundException) { return Results.NotFound(); }
        catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    /// <summary>
    /// Запись — если это вид работы; иначе <c>null</c> и 404 у звавшего. Проверка обязательна на КАЖДОМ
    /// адресе с идентификатором: без неё право на классификатор открывало бы правку любой записи общих
    /// данных. 404, а не 403: существование чужой записи за этой дверью не подтверждается.
    /// </summary>
    private static async Task<DomainObject?> WorkTypeAsync(Guid id, IMediator m, IRepository<DocumentType> types, CancellationToken ct)
    {
        var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
        if (entry is null || entry.IsDocument) return null;
        var family = CoreDirectories.Family(await types.GetAllAsync(ct), CoreRecordTypes.WorkTypeCode);
        return family is not null && family.ContainsKey(entry.CompositeTypeId) ? entry : null;
    }

    /// <summary>
    /// Версия, названная заголовком: нет — 400, устарела — 409 словами вида работы. Правка сверяет её ещё
    /// раз под блокировкой (команда), и опоздавшая правка получает общий текст «запись изменили».
    /// </summary>
    private static IResult? Seen(HttpRequest http, DomainObject entry, out string seen)
    {
        if (RecordIfMatch.Refuse(http, out seen) is { } refused) return refused;
        return string.Equals(entry.Version, seen.Trim(), StringComparison.Ordinal)
            ? null
            : Results.Conflict(new { error = Moved });
    }

    /// <summary>Совпадает ли вид работы с поиском: код, наименование, название записи, псевдонимы.</summary>
    private static bool Matches(DomainObject entry, string q, WorkTypeKeys keys)
    {
        var data = entry.Data.RootElement;
        return new[] { CoreDirectories.Text(data, keys.Code), CoreDirectories.Text(data, keys.Name), entry.DisplayName }
            .Concat(entry.Aliases)
            .Any(text => text?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    /// <summary>
    /// Уровень записи. Умолчание — «Система»: классификатор — компании, а не стройки (CORE-8). Уровень
    /// принимается, как у общего адреса: форма каталога стройки, заводящая вид, положила бы его иначе
    /// не туда, где его ищут.
    /// </summary>
    private static CatalogScope Scope(string? scope) => scope switch
    {
        "Set" => CatalogScope.Set,
        "Section" => CatalogScope.Section,
        "Construction" => CatalogScope.Construction,
        _ => CatalogScope.System,
    };

    private const string Moved =
        "Вид работы тем временем изменили, и действие не выполнено. Откройте его заново и повторите.";

    private const string DirectoryMissing =
        "Классификатор видов работ ещё не заведён: нет справочника единиц измерения, на который ссылается " +
        "вид работы. Заведите справочник единиц — классификатор появится при следующем запуске.";

    private const string DataRequired =
        "В запросе нет поля «data» с реквизитами. Правка заменяет вид работы целиком, поэтому пустое " +
        "значение стёрло бы его код, единицу и признаки. Пришлите реквизиты, которые должны остаться.";

    private const string NotAWorkType =
        "Названный тип — не вид работы: этим адресом заводятся только записи классификатора видов работ и его " +
        "подтипов. Записи других справочников заводятся общим адресом /api/common-data.";

    /// <summary>
    /// Тип необязателен: без него — сам классификатор. Названный обязан быть им или его подтипом —
    /// иначе форма каталога, заводящая запись подтипа, молча получила бы запись корневого типа.
    /// </summary>
    private record CreateWorkTypeRequest(
        string DisplayName, string? Data, string[]? Aliases, bool? CreateAnyway = null, string? Scope = null, Guid? ScopeId = null,
        Guid? RefsStandIn = null, Guid? CompositeTypeId = null);

    private record UpdateWorkTypeRequest(string DisplayName, string? Data, string[]? Aliases);
}
