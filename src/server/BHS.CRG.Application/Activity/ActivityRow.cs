namespace BHS.CRG.Application.Activity;

/// <summary>
/// Ряд журнала — серия записей одного вида в одной сессии правки (ТЗ CORE-28.2). Запись, которая ни с
/// чем не свернулась, — ряд из одной записи.
///
/// <para>Поля записи — от ГОЛОВЫ серии (кто, вид, когда начато), кроме метки цели: она от последней
/// записи, потому что цель за серию могла переименоваться (счёт № 14 стал № 41), и ряд обязан назвать
/// её нынешним именем. Прежнее — в <see cref="FirstTargetLabel" />.</para>
/// </summary>
/// <param name="Id">Идентификатор головы серии.</param>
/// <param name="OccurredAt">Время первой записи — по нему ряд и стоит на странице.</param>
/// <param name="TargetLabel">Метка цели ПОСЛЕДНЕЙ записи серии.</param>
/// <param name="Before">«Было», слитое по правилу вида (<see cref="ActivityFold" />).</param>
/// <param name="After">«Стало», слитое по правилу вида.</param>
/// <param name="Count">Сколько записей в серии.</param>
/// <param name="LastAt">Время последней записи; у одиночной совпадает с <paramref name="OccurredAt" />.</param>
/// <param name="FirstTargetLabel">Метка цели первой записи — только если она отличается от последней.</param>
/// <param name="SessionId">Сессия — ключ раскрытия ряда в записи.</param>
public sealed record ActivityRow(
    Guid Id, DateTimeOffset OccurredAt, string Action,
    Guid? ActorId, string ActorName,
    string? TargetId, string? TargetLabel, string? Before, string? After,
    int Count, DateTimeOffset LastAt, string? FirstTargetLabel, Guid SessionId);
