using System.Globalization;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Подсказки поля по ранее введённому (issue #1530, эпик #1527, контракт D10).
///
/// <para><b>На адресе — только вход, права — в службе</b> (D5, ТЗ AUTH-8): какие записи читать,
/// зависит от вида типов семьи поля, а он известен только после разбора схемы. Строка в корзине «ворота
/// внутри» переписи адресов называет сторожа.</para>
///
/// <para>⚠️ Ответы различаются нарочно: поле, которое не подсказывается, — 200 и пусто; неизвестный
/// тип или поле — 400; таймаут — 503, а не «пусто». Пустой список на таймауте читался бы как «ничего не
/// вводили», и поле молча замолкало бы там, где записей больше всего.</para>
/// </summary>
public static class FieldSuggestionEndpoints
{
    /// <summary>Сколько подсказок отдаётся без названного предела.</summary>
    private const int DefaultLimit = 8;

    /// <summary>Сколько подсказок можно попросить: больше — уже не подсказка, а выгрузка значений.</summary>
    private const int MaxLimit = 50;

    public static void MapFieldSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/field-suggestions", async (string? type, string? field, string? q, string? scope,
            string? scopeId, string? limit, ClaimsPrincipal user, DataAccessResolver access, IMediator m,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(type, out var typeId))
                return Bad("Назовите тип параметром type — идентификатором типа.");
            if (string.IsNullOrWhiteSpace(field))
                return Bad("Назовите поле параметром field — ключом поля в схеме типа.");

            // limit — строкой, как type и scopeId: число отвергла бы привязка, пустым 400 без нашего текста.
            var take = DefaultLimit;
            if (!string.IsNullOrEmpty(limit)
                && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out take) || take is < 1 or > MaxLimit))
                return Bad($"Параметр limit — от 1 до {MaxLimit}.");

            CatalogScope? level = null;
            if (!string.IsNullOrEmpty(scope))
            {
                if (!ScopeParameter.TryParse(scope, out var parsedLevel))
                    return Bad($"Неизвестный уровень scope: «{scope}».");
                level = parsedLevel;
            }
            Guid? scopeGuid = null;
            if (!string.IsNullOrEmpty(scopeId))
            {
                if (!Guid.TryParse(scopeId, out var parsed)) return Bad("Параметр scopeId — идентификатор уровня.");
                scopeGuid = parsed;
            }
            if (level is not null and not CatalogScope.System && scopeGuid is null)
                return Bad("Уровень назван без scopeId: своя стройка по нему не находится.");

            try
            {
                var items = await m.Send(new FieldSuggestionsQuery(typeId, field, q, level, scopeGuid,
                    take, await access.ForAsync(user, ct)), ct);
                return Results.Ok(new { items });
            }
            catch (SuggestionsTimedOutException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();
    }

    private static IResult Bad(string error) => Results.BadRequest(new { error });
}
