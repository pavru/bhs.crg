using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class CommonDataEndpoints
{
    public static void MapCommonDataEndpoints(this IEndpointRouteBuilder app)
    {
        // Чтение и запись — разными правами (см. CatalogEndpoints).
        var g = app.MapGroup("/api/common-data").RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogRead));
        var edit = app.MapGroup("/api/common-data").RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogEdit));
        var purge = app.MapGroup("/api/common-data").RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogPurge));

        // List — optional filters: scope, scopeId, typeId
        g.MapGet("/", async (string? scope, Guid? scopeId, Guid? typeId, string? purpose, IMediator m) =>
        {
            // Назначение обязательно и здесь: список уровня — это и страница справочника (показ), и
            // кандидаты базового экземпляра вне комплекта (выбор). Адрес один, ответы разные.
            if (Purpose(purpose) is not { } records) return PurposeRequired();
            CatalogScope? parsedScope = scope switch
            {
                "Set"          => CatalogScope.Set,
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => null,
            };
            return Results.Ok((await m.Send(new ListCommonDataEntriesQuery(records, parsedScope, scopeId, typeId)))
                .Select(CommonDataEntryDto.From)
                .Select(Elide));
        });

        // Resolve all relevant entries for a document set (full hierarchy)
        g.MapGet("/for-set/{setId:guid}", async (Guid setId, Guid? typeId, string? purpose, IMediator m) =>
        {
            if (Purpose(purpose) is not { } records) return PurposeRequired();
            try
            {
                return Results.Ok((await m.Send(new ResolveCommonDataForSetQuery(setId, records, typeId))).Select(Elide));
            }
            catch (NotFoundException ex) { return Results.NotFound(ex.Message); }
        });

        // Resolve entries visible from ANY scope level, walking the parent chain (issue #82).
        g.MapGet("/for-scope", async (string scope, Guid? scopeId, Guid? typeId, string? purpose, string? only, IMediator m) =>
        {
            if (Purpose(purpose) is not { } records) return PurposeRequired();
            // only=archived — раздел «В архиве» окна выбора: ему нужны одни архивные, а не весь
            // список уровня ради трёх записей (ревью PR #1229). Только с показом: у выбора архивных
            // нет, и пустой ответ выглядел бы как «в архиве ничего нет».
            var archivedOnly = only == "archived";
            if (only is not null && !(archivedOnly && records == RecordsFor.Display))
                return Results.BadRequest(new { error = "Параметр only принимает одно значение — archived — и только вместе с purpose=display." });
            CatalogScope? parsed = scope switch
            {
                "Set"          => CatalogScope.Set,
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => null,
            };
            if (parsed is null) return Results.BadRequest($"Unknown scope '{scope}'.");
            return Results.Ok((await m.Send(new ResolveCommonDataForScopeQuery(parsed.Value, scopeId, records, typeId, archivedOnly)))
                .Select(Elide));
        });

        // Какие из стоящих в форме ссылок указывают на архивные записи (issue #1185). POST — потому
        // что идентификаторов бывает сотня (таблица документа), а не потому, что адрес что-то меняет.
        g.MapPost("/archived-among", async (ArchivedAmongRequest req, IMediator m) =>
            Results.Ok(new { archived = await m.Send(new ArchivedAmongQuery(req.Ids ?? [])) }));

        // По идентификатору — ПОЛНАЯ запись, без отсечения: этот путь кормит редактор (issue #520).
        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id));
            return entry is null ? Results.NotFound() : Results.Ok(CommonDataEntryDto.From(entry));
        });

        // Аудит записи общих данных (issue #644). Тот же AuditInstanceQuery, что и у документа: он
        // работает над DomainObject, а запись общих данных — такой же объект. Форма читает отсюда
        // расхождения значений с типом; до этого их не показывал никто — сканер выпуска записей
        // общих данных не касается вовсе.
        g.MapGet("/{id:guid}/audit", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new AuditInstanceQuery(id))); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        // Парного `audit/apply` здесь НЕТ намеренно: форма записи (issue #644) только показывает
        // расхождения, а чинят их в аудите типа — тем же ApplyAuditFixesCommand, но через уже
        // существующий маршрут. Заводить второй пишущий маршрут, которого никто не зовёт, значит
        // держать непроверенную точку записи по общим данным уровня «Система».

        // Проверка связок (issue #99): сверка снимка $ref-ссылок со свежим резолвом источника.
        g.MapGet("/{id:guid}/binding-check", async (Guid id, IMediator m,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            try { return Results.Ok(await m.Send(new CheckCommonDataBindingsQuery(
                id, await access.ForAsync(user, ct)))); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        edit.MapPost("/", async (CreateRequest req, IMediator m) =>
        {
            var scope = req.Scope switch
            {
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => CatalogScope.Set,
            };
            try
            {
                return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand(
                    req.DisplayName, req.CompositeTypeId,
                    JsonDocument.Parse(req.Data), scope, req.ScopeId, req.Aliases, req.CreateAnyway ?? false,
                    req.RefsStandIn))));
            }
            // «Есть в архиве» — полями, а не словами (issue #1185): экран предлагает вернуть запись
            // кнопкой и повторить создание с createAnyway, и разбирать для этого фразу не должен.
            catch (ArchivedTwinException ex)
            {
                return Results.Conflict(new
                {
                    error = ex.Message, code = "archived-twin",
                    archivedId = ex.ArchivedId, archivedName = ex.ArchivedName, archivedScope = ex.ArchivedScope,
                });
            }
        });

        // Правка называет версию записи, по которой собрана (issue #1214): заменяется запись целиком,
        // и без версии из двух открытых форм молча побеждала сохранённая последней.
        edit.MapPut("/{id:guid}", async (Guid id, UpdateRequest req, HttpRequest http, IMediator m,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            if (RecordIfMatch.Refuse(http, out var seen) is { } refused) return refused;
            return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand(
                id, req.DisplayName, JsonDocument.Parse(req.Data),
                await access.ForAsync(user, ct), seen, req.Aliases))));
        });

        edit.MapDelete("/{id:guid}", async (
            Guid id, IMediator m, ClaimsPrincipal user, IUserPermissions permissions, CancellationToken ct) =>
        {
            try { await m.Send(new DeleteCommonDataEntryCommand(id)); return Results.NoContent(); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Выходы из отказа — полями, а не словами причины: экран предлагает их кнопками и не
            // должен ни разбирать фразу, ни звать туда, куда пути нет (issue #1185, #1187).
            catch (ConflictException ex) { return await RecordRefusal.ConflictAsync(ex, id, m, user, permissions, ct); }
        });

        // Принудительное удаление (issue #1187): запись держат только данные выключенного или снятого
        // модуля, и убрать ссылку негде. Свой адрес, а не параметр удаления: право видно переписи
        // ворот только на воротах. Сотрудник удаляется этим же адресом — запись та же.
        purge.MapPost("/{id:guid}/purge", async (
            Guid id, PurgeRequest req, IMediator m, ClaimsPrincipal user, IUserPermissions permissions,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await m.Send(new PurgeHeldRecordCommand(id, req.References))); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Число не совпало — в отказе свежее предложение: экран показывает его заново.
            catch (ConflictException ex)
            {
                return await RecordRefusal.ConflictAsync(ex, id, m, user, permissions, ct, offerArchive: false);
            }
            // Сюда обычное удаление не доходит: держателя оно встречает отказом раньше базы.
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
                when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation } pg)
            {
                return RecordRefusal.HeldByConstraint(pg.ConstraintName);
            }
        });

        // Архив — отдельными адресами, а не полем правки (issue #1185): форма, не знающая признака,
        // сняла бы его обычным сохранением. Право то же, что у правки: архив слабее удаления, и
        // своё право породило бы роль, которой удалять можно, а убрать из выбора нельзя.
        edit.MapPost("/{id:guid}/archive", (Guid id, IMediator m) => SetArchiveAsync(id, true, m));
        edit.MapPost("/{id:guid}/unarchive", (Guid id, IMediator m) => SetArchiveAsync(id, false, m));
    }

    private static async Task<IResult> SetArchiveAsync(Guid id, bool archived, IMediator m)
    {
        try { return Results.Ok(await m.Send(new SetRecordArchiveCommand(id, archived))); }
        catch (NotFoundException) { return Results.NotFound(); }
        catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    /// <summary>
    /// Назначение чтения из параметра адреса (issue #1185): <c>choice</c> — список на выбор, архивных
    /// записей в нём нет; <c>display</c> — показ, архивные на месте с признаком.
    ///
    /// <para>Умолчания нет НАРОЧНО: один и тот же адрес кормит и выбор значения, и показ уже
    /// стоящего, и ответы у них разные. Сервер, молча выбирающий за клиента, либо вернул бы архивную
    /// запись в выбор, либо оставил бы сохранённую ссылку без названия — и оба исхода выглядели бы
    /// исправной работой.</para>
    /// </summary>
    private static RecordsFor? Purpose(string? purpose) => purpose switch
    {
        "choice" => RecordsFor.Choice,
        "display" => RecordsFor.Display,
        _ => null,
    };

    private static IResult PurposeRequired() => Results.BadRequest(new
    {
        error = "Не названо назначение чтения: параметр purpose обязателен и принимает choice " +
                "(список на выбор — архивные записи скрыты) либо display (показ — архивные на месте).",
    });

    /// <summary>
    /// Списочный ответ без тяжёлой полезной нагрузки (issue #520). Вызывается ЯВНО в трёх списочных
    /// местах и никогда внутри <see cref="CommonDataEntryDto.From"/>: тот обслуживает и чтение одной
    /// записи, и ответы POST/PUT — отсечение там молча обрезало бы редактор и round-trip записи.
    /// </summary>
    private static CommonDataEntryDto Elide(CommonDataEntryDto dto) =>
        dto with { Data = HeavyLeafElision.WithoutHeavyLeaves(dto.Data) };

    private static CommonDataEntryWithScope Elide(CommonDataEntryWithScope entry) =>
        entry with { Data = HeavyLeafElision.WithoutHeavyLeaves(entry.Data) };

    record CreateRequest(string DisplayName, Guid CompositeTypeId, string Data, string Scope, Guid? ScopeId, string[]? Aliases,
        bool? CreateAnyway = null, Guid? RefsStandIn = null);
    record ArchivedAmongRequest(Guid[]? Ids);
    record UpdateRequest(string DisplayName, string Data, string[]? Aliases);
}
