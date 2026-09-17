namespace BHS.CRG.Infrastructure.Notifications;

/// <summary>
/// Когда вытеснение уведомлений заслуживает строки в журнале (issue #919).
///
/// Вытеснение само по себе — норма: корзина держит <c>MaxKept</c> записей, и при полной корзине
/// каждая публикация удаляет одну. Строка на каждую подрезку удвоила бы шум ровно во время потока.
/// Сигнал другой: вытеснена МОЛОДАЯ запись — вся корзина уместилась в несколько дней, значит, кто-то
/// публикует потоком. Такого издателя надо увидеть и лечить у себя, а не в хранилище.
/// </summary>
public static class NotificationEviction
{
    /// <summary>Корзина уложилась в меньший срок — это поток, а не ротация.</summary>
    public static readonly TimeSpan FlowWindow = TimeSpan.FromDays(7);

    /// <summary>Не чаще, чем раз в этот срок на корзину: поток публикует непрерывно.</summary>
    public static readonly TimeSpan WarnEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// Поток ли это. Оба времени — из записей в базе, а не часы процесса: возраст считается между
    /// самой свежей оставленной и самой молодой вытесненной.
    /// </summary>
    public static bool IsFlow(DateTimeOffset youngestEvicted, DateTimeOffset newestKept)
        => newestKept - youngestEvicted < FlowWindow;

    public static bool ShouldWarn(bool flow, DateTimeOffset? lastWarnedAt, DateTimeOffset now)
        => flow && (lastWarnedAt is null || now - lastWarnedAt.Value >= WarnEvery);
}
