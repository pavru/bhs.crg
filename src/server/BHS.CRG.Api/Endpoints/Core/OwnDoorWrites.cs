using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    /// <param name="guard">Отказ, который дверь проверяет перед записью: при <paramref name="exclusive" /> —
    /// под замком, в одной транзакции с архивом. Версия ответа читается ПОСЛЕ фиксации — иначе свежая
    /// область увидела бы прежнюю.</param>
    internal static async Task<IResult> SetArchiveAsync(
        Guid id, bool archived, IMediator m, IServiceScopeFactory scopes, CancellationToken ct,
        Func<Task<IResult?>>? guard = null, DoorLock? exclusive = null)
    {
        try
        {
            var (refused, done) = exclusive is null ? await WriteAsync() : await exclusive.RunAsync(async () =>
            {
                var outcome = await WriteAsync();
                return (outcome, Keep: outcome.Refused is null);
            }, ct);
            if (refused is not null) return refused;
            using var fresh = scopes.CreateScope();
            var after = await fresh.ServiceProvider.GetRequiredService<IMediator>().Send(new GetCommonDataEntryQuery(id), ct);
            return Results.Ok(new { done!.Id, done.DisplayName, done.Archived, done.Changed, version = after?.Version });
        }
        catch (NotFoundException) { return Results.NotFound(); }
        catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }

        async Task<(IResult? Refused, RecordArchiveResult? Done)> WriteAsync() =>
            guard is not null && await guard() is { } refusal
                ? (refusal, null)
                : (null, await m.Send(new SetRecordArchiveCommand(id, archived), ct));
    }
}

/// <summary>
/// Замок двери на правило, которого нет в базе (ревью PR #1966): «тёзки ещё нет» и запись идут под
/// совещательным замком в одной транзакции. Иначе два одновременных запроса оба видят «нет» и оба
/// пишут. Команды ядра своих транзакций не открывают — они пишут в эту.
///
/// <para>Тело решает, оставить ли записанное: отказ, найденный уже после записи (дверь сначала даёт
/// пройти охране записи, а «есть в архиве» говорит потом), откатывает транзакцию.</para>
/// </summary>
internal sealed class DoorLock(AppDbContext db, long key)
{
    public async Task<T> RunAsync<T>(Func<Task<(T Value, bool Keep)>> body, CancellationToken ct)
    {
        // Чужую транзакцию не открываем второй раз и не закрываем: замок тогда держит она.
        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        var (value, keep) = await body();
        if (own is not null)
        {
            if (keep) await own.CommitAsync(CancellationToken.None);
            else await own.RollbackAsync(CancellationToken.None);
        }
        return value;
    }
}
