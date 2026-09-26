using System.Text.Json;
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
/// </summary>
public static class EmployeeEndpoints
{
    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/employees")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.EmployeesRead));
        var edit = app.MapGroup("/api/employees")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.EmployeesEdit));

        g.MapGet("/", async (IMediator m, IRepository<DocumentType> types) =>
        {
            var typeId = await EmployeeTypeIdAsync(types);
            if (typeId is null) return Results.Ok(Array.Empty<CommonDataEntryDto>());

            return Results.Ok((await m.Send(new ListCommonDataEntriesQuery(CompositeTypeId: typeId)))
                .Select(CommonDataEntryDto.From)
                .Select(Elide));
        });

        // По идентификатору — карточка ЦЕЛИКОМ, без отсечения тяжёлых листьев: этот путь кормит
        // редактор, и обрезанная карточка вернулась бы в базу обрезанной (issue #520).
        g.MapGet("/{id:guid}", async (Guid id, IMediator m, IRepository<DocumentType> types) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id));
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();
            return Results.Ok(CommonDataEntryDto.From(entry));
        });

        edit.MapPost("/", async (CreateEmployeeRequest req, IMediator m, IRepository<DocumentType> types) =>
        {
            // Слой API отвечает КОДОМ, а не бросает доменный отказ (стережёт
            // DomainExceptionPolicyTests): бросок отсюда ушёл бы в общий конвейер и стал бы тем же
            // ответом, но правило одно на весь слой — иначе «где отвечают, а где бросают» пришлось
            // бы выяснять по каждому файлу.
            if (await EmployeeTypeIdAsync(types) is not { } typeId)
                return Results.Conflict(new { error = DirectoryMissing });

            return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand(
                req.DisplayName, typeId, JsonDocument.Parse(req.Data ?? "{}"),
                CatalogScope.System, ScopeId: null, req.Aliases))));
        });

        edit.MapPut("/{id:guid}", async (
            Guid id, UpdateEmployeeRequest req, IMediator m, IRepository<DocumentType> types) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id));
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();

            return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand(
                id, req.DisplayName, JsonDocument.Parse(req.Data ?? "{}"), req.Aliases))));
        });

        edit.MapDelete("/{id:guid}", async (Guid id, IMediator m, IRepository<DocumentType> types) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id));
            if (entry is null || !await IsEmployeeAsync(entry, types)) return Results.NotFound();

            try { await m.Send(new DeleteCommonDataEntryCommand(id)); return Results.NoContent(); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    private const string DirectoryMissing =
        "Справочник сотрудников ещё не заведён: нет типа-родителя «Персона», от которого он " +
        "производен. Заведите справочник лиц — сотрудники появятся при следующем запуске.";

    /// <summary>
    /// Тип справочника — по КОДУ (<see cref="CoreRecordTypes.EmployeeCode" />). Код постоянен, а имя
    /// администратор переименовывает — тем же правилом живёт список типов ядра.
    ///
    /// <para><c>null</c> — справочника в системе нет: на новой установке «Персоны» ещё не завели, и
    /// выводить из неё нечего. Список тогда пуст, а запись отвечает отказом, который называет
    /// причину: пустой список на попытку записи выглядел бы поломкой.</para>
    /// </summary>
    private static async Task<Guid?> EmployeeTypeIdAsync(IRepository<DocumentType> types)
    {
        var found = await types.FindAsync(t => t.Code == CoreRecordTypes.EmployeeCode);
        return found.Count > 0 ? found[0].Id : null;
    }

    /// <summary>
    /// Эта ли карточка из справочника сотрудников.
    ///
    /// <para>Проверка обязательна на КАЖДОМ адресе с идентификатором. Без неё дверь сотрудников
    /// открывала бы правку любой записи общих данных — организаций, единиц измерения, чего
    /// угодно, — то есть право <c>core.employees.edit</c> означало бы <c>core.catalog.edit</c>, и
    /// узнали бы об этом не сегодня. Отвечаем 404, а не 403: существование чужой карточки за этой
    /// дверью не подтверждается.</para>
    /// </summary>
    private static async Task<bool> IsEmployeeAsync(DomainObject entry, IRepository<DocumentType> types)
        => await EmployeeTypeIdAsync(types) is { } id && entry.CompositeTypeId == id;

    /// <summary>Списочный ответ без тяжёлой полезной нагрузки — как у общего каталога (issue #520).</summary>
    private static CommonDataEntryDto Elide(CommonDataEntryDto dto) =>
        dto with { Data = HeavyLeafElision.WithoutHeavyLeaves(dto.Data) };

    /// <summary>
    /// Тип в запросе НЕ принимается: он один и известен двери. Приди он снаружи — дверь сотрудников
    /// заводила бы записи любого типа, и право снова означало бы не то, что написано.
    /// </summary>
    private record CreateEmployeeRequest(string DisplayName, string? Data, string[]? Aliases);

    private record UpdateEmployeeRequest(string DisplayName, string? Data, string[]? Aliases);
}
