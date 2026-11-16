namespace BHS.CRG.Application.Activity;

/// <summary>
/// Автор фоновой задачи — на время её выполнения (ТЗ CORE-28.1, issue #1336, design D3): записи журнала,
/// сделанные внутри, подписаны заказчиком задачи и каналом того запроса, который её поставил, а не
/// «Системой».
///
/// <para><b>Окружением вызова (<see cref="AsyncLocal{T}" />), а не областью служб</b> — тем же приёмом, что
/// <c>SystemActivityScope</c> (work-08): автор журнала — синглтон (<c>HttpContextActivityActor</c>), и
/// подменить его в области задачи нечем; а окружение течёт через все <c>await</c> задачи и кончается вместе
/// с ней, так что соседняя задача в том же потоке автора не наследует.</para>
///
/// <para>⚠️ Действует только ВНЕ запроса: внутри запроса автор — вошедший, и окружение его не перекроет.
/// Открывает его исполнитель задач (<c>JobBackgroundService</c>) — одно место на все виды задач.</para>
/// </summary>
public static class ActivityActorScope
{
    private static readonly AsyncLocal<ActivityActor?> Value = new();

    /// <summary>Автор, назначенный окружением; <c>null</c> — не назначен.</summary>
    public static ActivityActor? Current => Value.Value;

    /// <summary>Подписать всё, что сделано до <c>Dispose</c>, автором <paramref name="actor" />.</summary>
    public static IDisposable Begin(ActivityActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var previous = Value.Value;
        Value.Value = actor;
        return new Restore(previous);
    }

    private sealed class Restore(ActivityActor? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}
