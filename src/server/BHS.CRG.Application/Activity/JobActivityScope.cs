namespace BHS.CRG.Application.Activity;

/// <summary>
/// Автор записей журнала фоновой задачи — на время её выполнения (ТЗ CORE-28.1, issue #1336, design D3):
/// записи, сделанные внутри, подписаны заказчиком задачи и каналом того запроса, который её поставил, а не
/// «Системой».
///
/// <para><b>Окружением вызова (<see cref="AsyncLocal{T}" />), а не областью служб</b> — тем же приёмом, что
/// <c>SystemActivityScope</c> (work-08): окружение течёт через все <c>await</c> задачи и кончается вместе с
/// ней, так что соседняя задача в том же потоке автора не наследует.</para>
///
/// <para>⚠️ <b>Действует ТОЛЬКО на журнал</b> (<c>ActivityLog</c>), а не на <see cref="IActivityActor" />.
/// Автор действия — это ещё и «кто действует» для портов модулей: <c>IModuleUser</c> и владелец задачи,
/// поставленной изнутри (<c>ModuleJobsPort</c>). Их договор вне запроса — «никого» (ревью PR #1997): подставь
/// туда человека — и работа модуля сочла бы себя запросом без прав, а дочерняя задача ушла бы в его
/// колокольчик вместо общесистемного уведомления. <c>SystemActivityScope</c> так делать вправе — он называет
/// систему, без идентификатора, и «никого» не нарушает.</para>
///
/// <para>Имя автора — с пометкой задачи (<see cref="ActivityActor.ForJob" />): записи задачи и ручная
/// правка того же человека — разные авторы, и сессию правки друг с другом не продолжают
/// (<see cref="ActivitySession" />). Открывает окружение исполнитель задач (<c>JobBackgroundService</c>) —
/// одно место на все виды задач; внутри запроса оно не открывается никогда.</para>
/// </summary>
public static class JobActivityScope
{
    private static readonly AsyncLocal<ActivityActor?> Value = new();

    /// <summary>Автор записей задачи; <c>null</c> — вне задачи или у задачи нет заказчика.</summary>
    public static ActivityActor? Author => Value.Value;

    /// <summary>Подписать записи журнала, сделанные до <c>Dispose</c>, автором <paramref name="author" />.</summary>
    public static IDisposable Begin(ActivityActor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        var previous = Value.Value;
        Value.Value = author;
        return new Restore(previous);
    }

    private sealed class Restore(ActivityActor? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}
