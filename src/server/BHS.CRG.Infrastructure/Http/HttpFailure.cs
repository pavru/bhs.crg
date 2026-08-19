namespace BHS.CRG.Infrastructure.Http;

/// <summary>
/// Отличает <b>отмену пользователем</b> от <b>таймаута HttpClient</b> (issue #797).
///
/// Оба приходят одним и тем же типом: <see cref="TaskCanceledException"/> — наследник
/// <see cref="OperationCanceledException"/>. Поэтому привычный фильтр
/// <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>, написанный ради
/// «не глотать отмену», заодно пропускал мимо себя и таймаут: движок распознавания не отвечал за
/// свои две минуты, исключение проходило сквозь движок и сквозь цепочку, обрывало перебор
/// остальных движков и доезжало до пользователя как 500 «Внутренняя ошибка сервера».
///
/// Различаем по токену, а не по <c>InnerException is TimeoutException</c>: внутреннее исключение —
/// деталь реализации <c>HttpClient</c> (появилась в .NET 5 и ставится не на всех путях отмены),
/// тогда как «токен вызывающего не отменён» — это ровно то, что нас интересует: <b>прервались не
/// по просьбе снаружи</b>, значит запрос стоит считать неудачей движка, а не отказом от работы.
///
/// Гонка «отменили ровно в момент таймаута» разрешается в пользу отмены — и это верно: результат
/// всё равно никому не нужен.
/// </summary>
public static class HttpFailure
{
    /// <summary>Прервано снаружи: запрос отменён вызывающим (ушёл пользователь, закрыт HTTP-запрос).</summary>
    public static bool IsUserCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>Истёк <see cref="HttpClient.Timeout"/> (или иной внутренний срок) при живом токене вызывающего.</summary>
    public static bool IsTimeout(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// Срок в тексте для пользователя. Сокращение «с» вместо слова: число берётся из настройки
    /// движка, а склонять «секунда/секунды/секунд» на сервере нечем — «за 121 секунд» хуже, чем «за 121 с».
    /// </summary>
    public static string Format(TimeSpan timeout) => $"{timeout.TotalSeconds:0} с";
}
