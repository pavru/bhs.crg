using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Принудительное удаление уровня — стройки, раздела, комплекта (issue #1243): адрес и тело отказа,
/// одни на три уровня.
///
/// <para>Выход из отказа экран узнаёт ПОЛЕМ <c>purge</c>, а не из слов причины, — как у записи
/// (<see cref="RecordRefusal" />). Рядом едет <c>leaving</c>: что уйдёт вместе с уровнем. Архива у
/// уровня нет, поэтому поля <c>canArchive</c> здесь нет вовсе.</para>
/// </summary>
public static class ScopePurge
{
    /// <summary>
    /// Адрес <c>POST …/{id}/purge</c>. Группа обязана стоять под ДВУМЯ правами — правкой этого уровня
    /// и <c>core.catalog.purge</c>: принудительное удаление остаётся удалением, и право «терять
    /// ссылки» не должно открывать стройки тому, кому их правка закрыта.
    /// </summary>
    public static void Map(RouteGroupBuilder purge, CatalogScope level) =>
        purge.MapPost("/{id:guid}/purge", async (
            Guid id, PurgeRequest req, IMediator m, ClaimsPrincipal user, IUserPermissions permissions,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await m.Send(new PurgeHeldScopeCommand(level, id, req.References), ct)); }
            catch (NotFoundException) { return Results.NotFound(); }
            // Число не совпало или состав изменился — в отказе свежее предложение.
            catch (ConflictException ex) { return await ConflictAsync(ex, user, permissions, ct, granted: true); }
            // У модуля счетов внешних ключей на таблицы ядра нет, но посторонняя схема может их
            // иметь: скан держателей такую колонку считает, а база удалить не даст.
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
                when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation } pg)
            {
                return RecordRefusal.HeldByConstraint(
                    pg.ConstraintName, "Ничего не удалено: на удаляемое ссылается внешний ключ базы");
            }
        });

    /// <summary>
    /// Отказ удаления уровня. Предложение есть, только если уровень держат одни выключенные или
    /// снятые модули: отказы ядра стоят раньше, и до этого места уровень с ними не доходит.
    ///
    /// <para>Само предложение составляет то же место, что у записи (<see cref="RecordRefusal.OfferAsync" />):
    /// правило «кому показывать разбивку» одно. Здесь добавляется только состав уходящего — и тоже
    /// лишь тому, у кого право есть.</para>
    /// </summary>
    /// <param name="granted">Право уже проверено группой адреса: читать его заново незачем.</param>
    public static async Task<IResult> ConflictAsync(
        ConflictException refusal, ClaimsPrincipal user, IUserPermissions permissions, CancellationToken ct,
        bool granted = false)
    {
        var offer = await RecordRefusal.OfferAsync(refusal, user, permissions, ct, granted);
        return offer is { Allowed: true } && refusal is ScopeHeldException held
            ? Results.Conflict(new { error = refusal.Message, purge = offer, leaving = held.Leaving })
            : Results.Conflict(new { error = refusal.Message, purge = offer });
    }
}
