namespace BHS.CRG.Application.Activity;

/// <summary>
/// Предыдущая запись той же цели — то, с чем новая запись решает, продолжает ли она сессию.
/// </summary>
/// <param name="ActorId">Автор предыдущей записи.</param>
/// <param name="Group">Группа свёртки её вида; <c>null</c> — вид не сворачивается.</param>
/// <param name="At">Когда она сделана.</param>
/// <param name="SessionStartedAt">Когда сделана первая запись её сессии.</param>
public sealed record ActivitySessionTail(Guid? ActorId, string? Group, DateTimeOffset At, DateTimeOffset SessionStartedAt);

/// <summary>
/// Граница сессии правки (ТЗ CORE-28.2) — одна на весь продукт.
///
/// <para>Сессия — подряд идущие записи ОДНОЙ цели одного автора, все из одной группы свёртки. Сравнение
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
    /// <param name="group">Группа свёртки нового вида; <c>null</c> — вид не сворачивается.</param>
    /// <param name="at">Время новой записи.</param>
    /// <param name="previous">Предыдущая запись цели; <c>null</c> — её нет.</param>
    /// <param name="companyZone">Пояс компании: полночь по нему сессию рвёт.</param>
    public static bool Continues(Guid? actorId, string? group, DateTimeOffset at,
        ActivitySessionTail? previous, TimeZoneInfo companyZone) =>
        previous is not null
        && actorId is not null && previous.ActorId == actorId
        && group is not null && previous.Group == group
        && at - previous.At <= MaxPause
        && at - previous.SessionStartedAt <= MaxLength
        && Day(at, companyZone) == Day(previous.At, companyZone);

    private static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
