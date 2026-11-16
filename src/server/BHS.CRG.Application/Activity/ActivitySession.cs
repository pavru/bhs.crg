namespace BHS.CRG.Application.Activity;

/// <summary>
/// Предыдущая запись той же цели — то, с чем новая запись решает, продолжает ли она сессию.
/// </summary>
/// <param name="ActorId">Автор предыдущей записи.</param>
/// <param name="ActorName">Его имя, как оно записано.</param>
/// <param name="Channel">Её канал (<c>ActivityChannel.Code</c>).</param>
/// <param name="Group">Группа свёртки её вида; <c>null</c> — вид не сворачивается.</param>
/// <param name="At">Когда она сделана.</param>
/// <param name="SessionStartedAt">Когда сделана первая запись её сессии.</param>
public sealed record ActivitySessionTail(Guid? ActorId, string ActorName, string Channel, string? Group, DateTimeOffset At, DateTimeOffset SessionStartedAt);

/// <summary>
/// Граница сессии правки (ТЗ CORE-28.2) — одна на весь продукт.
///
/// <para>Сессия — подряд идущие записи ОДНОЙ цели одного автора через один канал, все из одной группы
/// свёртки. Автор — снимок записи целиком, идентификатор И имя: задача, поставленная человеком, пишет от
/// его идентификатора, но под именем с пометкой задачи (<c>ActivityActor.ForJob</c>), и её записи с его
/// ручной правкой не склеиваются ни в какую сторону (ревью PR #1997). Сравнение
/// только с непосредственно предыдущей записью цели даёт «без чужих событий между» по построению: любая
/// запись той же цели вне группы или другого автора сама становится предыдущей и рвёт сессию.</para>
///
/// <para>⚠️ Окно одно для всех видов нарочно: разные окна у разных видов сделали бы ряд на экране
/// необъяснимым. Пороги взяты из разбора 27.10.2026 и уточняются замером, а не по месту.</para>
///
/// <para>⚠️ Токена сессии от клиента нет и не будет: порт журнала нарочно не принимает от вызывающего
/// ни автора, ни контекста (<c>IModuleActivityLog</c>) — «сессия экрана» стала бы таким параметром.</para>
/// </summary>
public static class ActivitySession
{
    /// <summary>Пауза между записями, после которой правка — уже новая сессия.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    /// <summary>Длина всей сессии: от первой записи до новой.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Продолжает ли новая запись сессию предыдущей записи той же цели. Границы включительно: ровно
    /// 10 минут паузы и ровно 60 минут сессии ещё продолжают её.
    /// </summary>
    /// <param name="actorId">Автор новой записи; <c>null</c> — сам экземпляр, такая запись одиночна.</param>
    /// <param name="actorName">Имя автора новой записи — таким, каким оно ляжет в запись.</param>
    /// <param name="channel">Канал новой записи. Другой канал рвёт сессию (issue #1336, design D9): ряд
    /// несёт поля головы, и серия, смешавшая каналы, спрятала бы при отборе по каналу запись агента внутри
    /// ряда, начатого в интерфейсе.</param>
    /// <param name="group">Группа свёртки нового вида; <c>null</c> — вид не сворачивается.</param>
    /// <param name="at">Время новой записи.</param>
    /// <param name="previous">Предыдущая запись цели; <c>null</c> — её нет.</param>
    /// <param name="companyZone">Пояс компании: полночь по нему сессию рвёт.</param>
    public static bool Continues(Guid? actorId, string actorName, string channel, string? group, DateTimeOffset at,
        ActivitySessionTail? previous, TimeZoneInfo companyZone) =>
        previous is not null
        && actorId is not null && previous.ActorId == actorId && previous.ActorName == actorName
        && previous.Channel == channel
        && group is not null && previous.Group == group
        && at - previous.At <= MaxPause
        && at - previous.SessionStartedAt <= MaxLength
        && Day(at, companyZone) == Day(previous.At, companyZone);

    private static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
