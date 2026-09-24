using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Settings;
using BHS.CRG.Modules;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Стройки и разделы — справочник ЯДРА (ТЗ CORE-5, issue #960).
///
/// <para>Почему отдельным файлом. До 0.192.0 эти адреса стояли в одном методе с адресами комплектов
/// документов, и обёртка модуля исполнительной документации не могла забрать комплекты, не забрав
/// заодно стройки: выключение модуля унесло бы общий справочник, на котором стоит весь
/// <c>CatalogScope</c>. Разрез обещан прямо в <see cref="Modules.IdModule" /> — здесь он
/// выполнен.</para>
///
/// <para>⚠️ Стройка и раздел принадлежат ядру, а комплект — модулю, и граница проходит РОВНО здесь:
/// всё, что ниже раздела, регистрирует модуль. Поэтому создание комплекта, прежде висевшее на
/// <c>POST /api/sections/{id}/sets</c>, переехало к комплектам
/// (<see cref="DocumentSetEndpoints" />): под префиксом ядра оно пережило бы выключение модуля.</para>
/// </summary>
public static class ConstructionEndpoints
{
    public static void MapConstructionEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Стройки ────────────────────────────────────────────────────────────
        // Чтение и запись — разными правами: справочник строек видят все рабочие роли, а правят
        // его единицы. Одно право на группу оставило бы core.constructions.edit без единой двери.
        var c = app.MapGroup("/api/constructions").RequireAuthorization(AppPolicies.Permission(CorePermissions.ConstructionsRead));
        var cEdit = app.MapGroup("/api/constructions").RequireAuthorization(AppPolicies.Permission(CorePermissions.ConstructionsEdit));

        c.MapGet("/", async (IMediator m, ClaimsPrincipal user, IDomainObjectRepository objRepo, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            var list = await m.Send(new ListConstructionsQuery(userId));
            var setIds = list.SelectMany(x => x.Sections).SelectMany(s => s.DocumentSets).Select(ds => ds.Id).ToList();
            var counts = await objRepo.CountDocumentsInSetsAsync(setIds, ct);
            return Results.Ok(list.Select(x => ConstructionDto.From(x, counts)).ToList());
        });

        c.MapGet("/{id:guid}", async (Guid id, IMediator m, IDomainObjectRepository objRepo, CancellationToken ct) =>
        {
            var construction = await m.Send(new GetConstructionQuery(id));
            if (construction is null) return Results.NotFound();
            var setIds = construction.Sections.SelectMany(s => s.DocumentSets).Select(ds => ds.Id).ToList();
            var counts = await objRepo.CountDocumentsInSetsAsync(setIds, ct);
            return Results.Ok(ConstructionDto.From(construction, counts));
        });

        cEdit.MapPost("/", async (CreateConstructionRequest req, IMediator m, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            return Results.Ok(await m.Send(new CreateConstructionCommand(req.Name, userId)));
        });

        cEdit.MapPut("/{id:guid}", async (Guid id, RenameRequest req, IMediator m)
            => Results.Ok(await m.Send(new RenameConstructionCommand(id, req.Name))));

        cEdit.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteConstructionCommand(id));
            return Results.NoContent();
        });

        // Пояс и внешний идентификатор — СВОИМИ адресами, а не полями в теле переименования
        // (ТЗ CORE-5). Сложи их в одно тело — и клиент, присылающий только имя, снимал бы пояс
        // каждым переименованием: отсутствие поля неотличимо от «убрать».
        cEdit.MapPut("/{id:guid}/timezone", async (Guid id, TimeZoneRequest req, IMediator m) =>
        {
            // Пустое значение — «как у компании», это разрешённый выбор. Непустое обязано
            // разбираться ЗДЕСЬ: иначе отказ придёт при первом подсчёте суток, далеко от ввода.
            if (!string.IsNullOrWhiteSpace(req.TimeZoneId) && !AppSettingKeys.IsKnownTimeZone(req.TimeZoneId))
                return Results.BadRequest(new { error = $"Часовой пояс «{req.TimeZoneId}» этой системе неизвестен." });
            return Results.Ok(await m.Send(new SetConstructionTimeZoneCommand(id, req.TimeZoneId)));
        });

        cEdit.MapPut("/{id:guid}/external-id", async (Guid id, ExternalIdRequest req, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new SetConstructionExternalIdCommand(id, req.System, req.Code)));
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                   { SqlState: "23505", ConstraintName: "IX_constructions_ExternalSystem_ExternalCode" })
            {
                // Проиграли гонку: пару заняли между проверкой в обработчике и записью. Отказ тот
                // же по смыслу — просто его вынесла база, у которой окна между проверкой и записью
                // нет. Ловится ИМЕННО этот индекс: чужое столкновение, объявленное «код занят»,
                // увело бы разбирательство в сторону. Без ветки ответом был бы 500, потому что
                // ApiErrorMapping прячет всё, что не наш отказ (ревью PR #1046).
                return Results.Conflict(new { error = "Этот код в этой системе только что занят другой стройкой." });
            }
        });

        // ── Разделы ────────────────────────────────────────────────────────────
        cEdit.MapPost("/{constructionId:guid}/sections", async (Guid constructionId, CreateSectionRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateSectionCommand(constructionId, req.Name))));

        var sEdit = app.MapGroup("/api/sections").RequireAuthorization(AppPolicies.Permission(CorePermissions.ConstructionsEdit));

        sEdit.MapPut("/{id:guid}", async (Guid id, RenameRequest req, IMediator m)
            => Results.Ok(await m.Send(new RenameSectionCommand(id, req.Name))));

        sEdit.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteSectionCommand(id));
            return Results.NoContent();
        });
    }

    static Guid GetUserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);

    record TimeZoneRequest(string? TimeZoneId);
    record ExternalIdRequest(string? System, string? Code);
    record CreateConstructionRequest(string Name);
    record CreateSectionRequest(string Name);
    record RenameRequest(string Name);
}
