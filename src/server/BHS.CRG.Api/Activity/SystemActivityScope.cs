using BHS.CRG.Application.Activity;

namespace BHS.CRG.Api.Activity;

/// <summary>
/// Чем занята система — на время одной фоновой работы (ТЗ WORK-41, work-08): записи журнала, сделанные
/// внутри, подписаны «Система — <c>что</c>», а не просто «Система».
///
/// <para><b>Окружением вызова (<see cref="AsyncLocal{T}" />), а не областью служб.</b> Автор журнала —
/// синглтон (<see cref="HttpContextActivityActor" />), и подменить его в области задачи нечем; а
/// окружение вызова течёт через все <c>await</c> обработчика и кончается вместе с ним, так что
/// соседняя задача в том же потоке подписи не наследует.</para>
///
/// <para>⚠️ Действует только ВНЕ запроса: внутри запроса автор — вошедший, и выдать его действие за
/// системное значило бы спрятать человека.</para>
/// </summary>
public static class SystemActivityScope
{
    private static readonly AsyncLocal<ActivityActor?> Current = new();

    /// <summary>Автор, назначенный окружением; <c>null</c> — не назначен.</summary>
    internal static ActivityActor? Actor => Current.Value;

    /// <summary>Подписать всё, что сделано до <c>Dispose</c>, как «Система — <paramref name="what" />».</summary>
    public static IDisposable Begin(string what)
    {
        if (string.IsNullOrWhiteSpace(what))
            throw new ArgumentException("Система обязана назвать, чем занята: иначе подпись — просто «Система».", nameof(what));

        var previous = Current.Value;
        Current.Value = new ActivityActor(null, $"{ActivityActor.System.Name} — {what}");
        return new Restore(previous);
    }

    private sealed class Restore(ActivityActor? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
