using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Справочник сотрудников — дверь ядра (ТЗ CORE-7, issue #962, часть 2 из 2).
///
/// <para>Сотрудник хранится объектом общего типа (CORE-16, класс A), поэтому здесь нет ни своих
/// команд, ни своего хранилища: дверь тонкая и зовёт те же <c>CommonData</c>-команды, что и общий
/// каталог. Своя она ради ПРАВА: <c>core.employees.*</c> выдаётся отдельно от
/// <c>core.catalog.*</c>, и без этого маршрута право было бы галкой, которая ничего не делает
/// (до этой задачи оно ровно ею и было — выдавалось двум системным ролям и лежало в списке
/// неиспользуемых).</para>
///
/// <para>⚠️ <b>Право управляет ПОРЯДКОМ, а не тайной</b> (ТЗ CORE-18). Те же карточки сегодня
/// отдаёт <c>/api/common-data</c> под <c>core.catalog.read</c> — объекты ЛЮБОГО составного типа, —
/// и пока пути чтения не разведены (работа STG-11, ТЗ TYPE-5 «видимость не действует»), закрыть
/// сотрудника этой дверью нельзя. Это записано и в объяснении самого права: обещать разграничение,
/// которого нет, хуже, чем не обещать ничего.</para>
///
/// <para>Уровень — <see cref="CatalogScope.System" />: сотрудник принадлежит компании, а не
/// стройке. Уровень здесь про адресацию данных, а не про доступ (инвариант проекта).</para>
///
/// <para><b>Единственная дверь записи</b> (решение владельца по C15, 11.11.2026, change
/// <c>work-01-employee-account</c>): общий адрес справочников записи сотрудников не пишет
/// (<see cref="OwnDoorTypes" />), поэтому здесь есть всё, что было у него, — семья типа (подтипы
/// «Сотрудника»), архив и возврат, вынос в общие данные. Версия — на каждом пишущем адресе, кроме
/// заведения: удаление и архив сверяют её, как у видов работ (ТЗ AUTH-9).</para>
/// </summary>
public static class EmployeeEndpoints
{
    /// <summary>Адрес двери — его же называет отказ общего адреса.</summary>
    public const string Door = "/api/employees";

    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.EmployeesRead));
        var edit = app.MapGroup(Door)
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.EmployeesEdit));

        g.MapGet("/", async (IMediator m, IRepository<DocumentType> types, EmployeeAccountReads accounts,
            CancellationToken ct) =>
        {
            var all = await types.GetAllAsync(ct);
            // Справочника нет — отказ с причиной, а не пустой список: «сотрудников нет» и «справочник
            // не заведён» — разные состояния, и у второго есть выход («Завести»), у первого — нет.
            if (Family(all) is not { } family) return DirectoryMissingResult(all);

            // Страница сотрудников — показ: архивная карточка на месте, с признаком (issue #1185).
            var employees = new List<DomainObject>();
            foreach (var typeId in family.Keys)
                employees.AddRange(await m.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId), ct));
            // Состояние связи — у каждого, одним запросом на всех (ТЗ WORK-47, D15): экран его не выводит.
            var linked = await accounts.ForAsync(employees, ct);
            return Results.Ok(employees
                .Select(e => new EmployeeEntryDto(RecordRequests.Elide(CommonDataEntryDto.From(e)), linked[e.Id])));
        });

        // По идентификатору — карточка ЦЕЛИКОМ, без отсечения тяжёлых листьев: этот путь кормит
        // редактор, и обрезанная карточка вернулась бы в базу обрезанной (issue #520).
        g.MapGet("/{id:guid}", async (Guid id, IMediator m, IRepository<DocumentType> types,
            EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();
            return Results.Ok(await EmployeeEntryDto.OfAsync(entry, accounts, ct));
        });

        // Связь с учётной записью, кандидаты, сопоставление и «Завести» (work-01) — всё под правкой.
        edit.MapEmployeeAccountEndpoints();

        edit.MapPost("/", async (CreateEmployeeRequest req, IMediator m, IRepository<DocumentType> types,
            EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            // Слой API отвечает КОДОМ, а не бросает доменный отказ (стережёт
            // DomainExceptionPolicyTests): бросок отсюда ушёл бы в общий конвейер и стал бы тем же
            // ответом, но правило одно на весь слой — иначе «где отвечают, а где бросают» пришлось
            // бы выяснять по каждому файлу.
            var all = await types.GetAllAsync(ct);
            if (Family(all) is not { } family) return DirectoryMissingResult(all);
            // Тип — сам справочник либо его подтип; иной тип дверь не заводит, иначе право на
            // сотрудников открывало бы заведение записей любого справочника.
            var typeId = req.CompositeTypeId ?? family.Values.First(t => t.Code == CoreRecordTypes.EmployeeCode).Id;
            if (!family.ContainsKey(typeId)) return Results.BadRequest(new { error = NotAnEmployee });

            // Новая карточка без данных осмысленна: реквизиты заполнят следом. Негодный JSON — нет:
            // это 400 с указанием поля, а не 500 «внутренняя ошибка» (см. RecordRequests.ParseData).
            if (RecordRequests.ParseData(req.Data) is not { } data)
                return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            // Ответ записи — той же формы, что ответ чтения, с состоянием связи (ТЗ WORK-47): экран,
            // кладущий ответ сохранения на место прочитанной карточки, иначе потерял бы связь.
            try
            {
                var created = await m.Send(new CreateCommonDataEntryCommand(
                    req.DisplayName, typeId, data, CatalogScope.System, ScopeId: null, req.Aliases,
                    req.CreateAnyway ?? false, req.RefsStandIn), ct);
                return Results.Ok(await EmployeeEntryDto.OfAsync(created, accounts, ct));
            }
            // Тот же ответ, что у общего адреса создания (issue #1185): сотрудник с таким табельным
            // номером лежит в архиве. Без полей и без createAnyway отказ был бы тупиком — текст
            // предлагает «создать, подтвердив», а подтвердить было бы нечем (ревью PR #1229).
            catch (ArchivedTwinException ex) { return RecordRequests.ArchivedTwin(ex); }
        });

        edit.MapPut("/{id:guid}", async (
            Guid id, UpdateEmployeeRequest req, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            ClaimsPrincipal user, DataAccessResolver access, EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();
            // Версия записи — как у общего адреса общих данных (issue #1214): сотрудник — та же
            // запись. После «не найдено»: чужой карточке незачем объяснять, как её править.
            if (Documents.RecordIfMatch.Refuse(http, out var seen) is { } refused) return refused;

            // ⚠️ Отсутствующее «data» — ОТКАЗ, а не пустой объект (нашло ревью PR #1053). Правка
            // заменяет реквизиты целиком, поэтому подстановка «{}» молча стирала бы карточку и
            // отвечала 200: улетели бы табельный номер (по нему сотрудника находит бухгалтерия),
            // период работы и унаследованное ФИО. Охрана записи тут не спасает — обязательность она
            // не проверяет, а замок на связи срабатывает только если значение там БЫЛО.
            if (req.Data is null) return Results.BadRequest(new { error = DataRequired });
            if (RecordRequests.ParseData(req.Data) is not { } data)
                return Results.BadRequest(new { error = RecordRequests.DataNotJson });

            var updated = await m.Send(new UpdateCommonDataEntryCommand(
                // Псевдонимы, о которых запрос молчит, ОСТАЮТСЯ: null здесь означает «очистить»
                // (DomainObject.Update нормализует его в пустой список), и промолчавший клиент
                // стирал бы их заодно с правкой одного поля.
                id, req.DisplayName, data, await access.ForAsync(user, ct), seen,
                req.Aliases ?? [.. entry.Aliases]), ct);
            return Results.Ok(await EmployeeEntryDto.OfAsync(updated, accounts, ct));
        });

        edit.MapDelete("/{id:guid}", async (
            Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types, ClaimsPrincipal user,
            IUserPermissions permissions, CancellationToken ct) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();
            if (Seen(http, entry) is { } refused) return refused;

            try { await m.Send(new DeleteCommonDataEntryCommand(id), ct); return Results.NoContent(); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Тело отказа — то же, что у общего адреса: выходы «в архив» и «удалить, потеряв ссылки»
            // у сотрудника те же (issue #1187).
            catch (ConflictException ex) { return await RecordRefusal.ConflictAsync(ex, id, m, user, permissions, ct); }
        });

        // Архив — своим адресом: общий отправку сотрудника в архив отвергает (C15).
        edit.MapPost("/{id:guid}/archive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, true, http, m, types, scopes, ct));
        edit.MapPost("/{id:guid}/unarchive", (Guid id, HttpRequest http, IMediator m, IRepository<DocumentType> types,
            IServiceScopeFactory scopes, CancellationToken ct) => SetArchiveAsync(id, false, http, m, types, scopes, ct));
    }

    /// <summary>
    /// Ответ архива несёт НОВУЮ версию: архив её сдвигает, и экран, предлагающий «Вернуть» тостом,
    /// без неё назвал бы прежнюю — и получил бы отказ на собственное действие. Версия читается в
    /// СВЕЖЕЙ области: архив пишет признак мимо отслеживания, и запись, прочитанная для сверки, в
    /// контексте запроса осталась бы прежней (то же у видов работ).
    /// </summary>
    private static async Task<IResult> SetArchiveAsync(
        Guid id, bool archived, HttpRequest http, IMediator m, IRepository<DocumentType> types,
        IServiceScopeFactory scopes, CancellationToken ct)
    {
        var entry = await m.Send(new GetCommonDataEntryQuery(id), ct);
        if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();
        if (Seen(http, entry) is { } refused) return refused;

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
    /// Версия, названная заголовком: нет — 400, устарела — 409. У удаления и архива параметра версии
    /// нет, поэтому сверка — до команды; правка содержимого в окно между ними не попадает: архив и
    /// удаление содержимого не пишут, а успевшая раньше правка сверку не проходит.
    /// </summary>
    private static IResult? Seen(HttpRequest http, DomainObject entry)
    {
        if (Documents.RecordIfMatch.Refuse(http, out var seen) is { } refused) return refused;
        return RecordSeen.Matches(entry.Version, seen) ? null : Results.Conflict(new { error = RecordSeen.Moved });
    }

    private const string DataRequired =
        "В запросе нет поля «data» с реквизитами. Правка заменяет карточку целиком, поэтому пустое " +
        "значение стёрло бы её — включая табельный номер и период работы. Пришлите реквизиты, " +
        "которые должны остаться.";

    private const string NotAnEmployee =
        "Этот тип — не справочник сотрудников: дверь сотрудников заводит только «Сотрудника» и его подтипы.";

    /// <summary>Нет типа «Персона», от которого справочник производен.</summary>
    public const string PersonMissing = "person-missing";

    /// <summary>«Персона» есть, а справочник не заведён — выход «Завести».</summary>
    public const string NotCreated = "not-created";

    /// <summary>
    /// Отказ «справочник не заведён» с причиной (задача 2.2): без «Персоны» «Завести» не поможет, с ней
    /// — поможет, и экран обязан различать эти два случая. «Персона» ищется так же, как её ищет
    /// «Завести» (без учёта регистра), — иначе экран предлагал бы кнопку, которая откажет.
    /// </summary>
    internal static IResult DirectoryMissingResult(IReadOnlyList<DocumentType> all)
    {
        var person = all.Any(t => string.Equals(t.Code, CoreRecordTypes.PersonCode, StringComparison.OrdinalIgnoreCase));
        return Results.Conflict(new
        {
            code = EmployeeAccountReads.DirectoryMissing,
            reason = person ? NotCreated : PersonMissing,
            error = person ? NotCreatedText : PersonMissingText,
        });
    }

    private const string NotCreatedText =
        "Справочник сотрудников ещё не заведён. Заведите его кнопкой «Завести» на экране «Сотрудники» — " +
        "перезапуск не нужен.";

    private const string PersonMissingText =
        "Справочник сотрудников ещё не заведён: нет типа «Персона», от которого он производен. Заведите " +
        "«Персону» в «Типах документов», затем — справочник кнопкой «Завести».";

    /// <summary>
    /// Справочник с подтипами — по КОДУ корня (<see cref="CoreRecordTypes.EmployeeCode" />), буква в
    /// букву. Код постоянен, а имя администратор переименовывает — тем же правилом живёт список типов
    /// ядра. Подтип входит потому, что общий адрес закрыт для всей семьи (<see cref="OwnDoorTypes" />):
    /// без него запись подтипа не писал бы никто.
    ///
    /// <para><c>null</c> — справочника в системе нет: на новой установке «Персоны» ещё не завели, и
    /// выводить из неё нечего.</para>
    /// </summary>
    internal static IReadOnlyDictionary<Guid, DocumentType>? Family(IReadOnlyList<DocumentType> all) =>
        CoreDirectories.Family(all, CoreRecordTypes.EmployeeCode);

    internal static async Task<IReadOnlyDictionary<Guid, DocumentType>?> FamilyAsync(
        IRepository<DocumentType> types, CancellationToken ct = default) => Family(await types.GetAllAsync(ct));

    /// <summary>
    /// Эта ли карточка из справочника сотрудников.
    ///
    /// <para>Проверка обязательна на КАЖДОМ адресе с идентификатором. Без неё дверь сотрудников
    /// открывала бы правку любой записи общих данных — организаций, единиц измерения, чего
    /// угодно, — то есть право <c>core.employees.edit</c> означало бы <c>core.catalog.edit</c>, и
    /// узнали бы об этом не сегодня. Отвечаем 404, а не 403: существование чужой карточки за этой
    /// дверью не подтверждается.</para>
    /// </summary>
    internal static async Task<bool> IsEmployeeAsync(DomainObject entry, IRepository<DocumentType> types)
        => !entry.IsDocument && await FamilyAsync(types) is { } family && family.ContainsKey(entry.CompositeTypeId);

    /// <summary>
    /// Тип в запросе — только из семьи «Сотрудника» (по умолчанию — корень): иначе право на
    /// сотрудников заводило бы записи любого типа. Уровень не принимается: сотрудник — компании.
    /// </summary>
    private record CreateEmployeeRequest(
        string DisplayName, string? Data, string[]? Aliases, bool? CreateAnyway = null,
        Guid? CompositeTypeId = null, Guid? RefsStandIn = null);

    private record UpdateEmployeeRequest(string DisplayName, string? Data, string[]? Aliases);
}
