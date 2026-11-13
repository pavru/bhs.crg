using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Общее у дверей справочников ядра, которые пишут записи общих данных своим адресом под своим
/// правом (<see cref="OwnDoorTypes" />): сверка версии из <c>If-Match</c> и отправка в архив с новой
/// версией в ответе. Одно тело на двери видов работ и вариантов работ (work-06), а не копия в каждой:
/// экран узнаёт ответы по полям, и дверь, ответившая иначе, оставила бы его без кнопки.
///
/// <para>⚠️ Чьей записи этот помощник НЕ проверяет: найти запись своей семьи и ответить 404 на чужую —
/// дело двери, до вызова. Без этой проверки право на справочник открывало бы любую запись общих данных.</para>
/// </summary>
internal static class OwnDoorWrites
{
    /// <summary>
    /// Версия, названная заголовком: нет — 400, устарела — 409 словами справочника
    /// (<paramref name="moved" />). Правка сверяет её ещё раз под блокировкой (команда), и опоздавшая
    /// правка получает общий текст «запись изменили».
    ///
    /// <para>У удаления и архива параметра версии нет, поэтому сверка — до команды; правка содержимого
    /// в окно между ними не попадает: архив и удаление содержимого не пишут, а успевшая раньше правка
    /// сверку не проходит.</para>
    /// </summary>
    internal static IResult? Seen(HttpRequest http, DomainObject entry, string moved, out string seen)
    {
        if (Documents.RecordIfMatch.Refuse(http, out seen) is { } refused) return refused;
        return RecordSeen.Matches(entry.Version, seen) ? null : Results.Conflict(new { error = moved });
    }

    /// <summary>
    /// Архив или возврат уже найденной и сверенной записи. Ответ несёт НОВУЮ версию: архив её сдвигает,
    /// и экран, предлагающий «Вернуть» тостом, без неё назвал бы прежнюю — и получил бы отказ на
    /// собственное действие.
    ///
    /// <para>⚠️ Версия читается в СВЕЖЕЙ области. Архив пишет признак условным обновлением мимо
    /// отслеживания, а запись, прочитанная звавшим для сверки, осталась в контексте запроса — повторное
    /// чтение там отдало бы её же, с прежней версией (поймано тестом видов работ).</para>
    /// </summary>
    internal static async Task<IResult> SetArchiveAsync(
        Guid id, bool archived, IMediator m, IServiceScopeFactory scopes, CancellationToken ct)
    {
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
}
