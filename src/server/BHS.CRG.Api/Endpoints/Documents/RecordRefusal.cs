using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Тело отказа удаления записи справочника — одно на все адреса, которые её удаляют (issue #1187).
///
/// <para>Выходы из отказа экран предлагает кнопками и узнаёт о них ПОЛЯМИ, а не из слов причины:
/// «можно в архив» (<c>canArchive</c>, issue #1185) и «можно удалить, потеряв ссылки»
/// (<c>purge</c>). Составляется в одном месте потому, что адресов удаления два — общие данные и
/// сотрудники, — и второй однажды уже отвечал одними словами: выход был, а узнать о нём было
/// неоткуда.</para>
/// </summary>
public static class RecordRefusal
{
    public static async Task<IResult> ConflictAsync(
        ConflictException refusal, Guid id, IMediator mediator, ClaimsPrincipal user,
        IUserPermissions permissions, CancellationToken ct) =>
        Results.Conflict(new
        {
            error = refusal.Message,
            canArchive = await mediator.Send(new CanArchiveRecordQuery(id), ct),
            purge = await OfferAsync(refusal, user, permissions, ct),
        });

    /// <summary>
    /// Предложение принудительного удаления — или <c>null</c>, если его нет: запись держит включённый
    /// модуль или само ядро, либо проверить держателей не удалось.
    ///
    /// <para>Отказы ядра сюда не доходят сами: «держат данные модулей» — ПОСЛЕДНИЙ отказ удаления, и
    /// раз он прозвучал, остальные уже пройдены.</para>
    ///
    /// <para>Тому, у кого права нет, предложение приходит без разбивки и с <c>allowed: false</c>:
    /// экран говорит «может администратор», а не молчит о выходе, который есть.</para>
    /// </summary>
    private static async Task<PurgeOffer?> OfferAsync(
        ConflictException refusal, ClaimsPrincipal user, IUserPermissions permissions, CancellationToken ct)
    {
        if (refusal is not RecordHeldException { Holdings.Release: { } release }) return null;

        if (!(await permissions.ForAsync(user, ct)).Contains(CorePermissions.CatalogPurge))
            return new PurgeOffer(Allowed: false, release.References, Untraceable: 0, Holders: []);

        return new PurgeOffer(Allowed: true, release.References, release.Untraceable,
            [.. release.Holders.Select(h => new PurgeHolder(h.Owner, h.What, h.Rows, h.Documents, h.Traceable))]);
    }
}

/// <param name="Allowed">Есть ли у спрашивающего право удалить, потеряв ссылки.</param>
/// <param name="References">Сколько ссылок будет потеряно — это число человек вводит.</param>
/// <param name="Untraceable">Сколько из них после удаления не покажет никто.</param>
public sealed record PurgeOffer(bool Allowed, int References, int Untraceable, IReadOnlyList<PurgeHolder> Holders);

/// <summary>Строка разбивки: чьи данные, что именно и сколько.</summary>
public sealed record PurgeHolder(string Owner, string What, int Rows, string? Documents, bool Traceable);

/// <summary>Подтверждение принудительного удаления: число теряемых ссылок, как его увидел человек.</summary>
public sealed record PurgeRequest(int References);
