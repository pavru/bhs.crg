using BHS.CRG.Domain.Activity;

namespace BHS.CRG.Application.Activity;

/// <summary>Автор записи журнала: учётная запись или сам экземпляр — и путь, которым он действует.</summary>
/// <param name="Id">null — действие сделал не человек: старт приложения, расписание, фоновая служба.</param>
/// <param name="Name">Имя на момент действия; у системы — <see cref="ActivityActor.System" />.</param>
/// <param name="Channel">Канал действия (ТЗ CORE-28.1, issue #1336). Часть автора, а не параметр записи:
/// писатель журнала его не передаёт и подделать не может — то же правило, что у самого автора (design D1).</param>
public sealed record ActivityActor(Guid? Id, string Name, ActivityChannel Channel)
{
    /// <summary>Автор для того, что делает сам экземпляр. Имя записывается словом, а не пустотой:
    /// пустое имя в журнале читается как «не смогли определить», а это другое утверждение.</summary>
    public static readonly ActivityActor System = new(null, "Система", ActivityChannel.System);

    /// <summary>
    /// Имя автора-человека: отображаемое имя, потом почта, потом идентификатор. Одно правило на автора
    /// запроса (из токена) и автора фоновой задачи (из учётной записи): разойдясь, они подписали бы одного
    /// человека в журнале по-разному. Пустая строка читалась бы как «автор неизвестен», хотя он известен.
    /// </summary>
    public static string NameOf(Guid? id, string? displayName, string? email) =>
        Nonblank(displayName) ?? Nonblank(email) ?? id?.ToString() ?? System.Name;

    private static string? Nonblank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Кто сейчас действует. Отдельной службой потому, что журнал живёт ниже слоя HTTP, а автор —
/// в запросе: тянуть <c>HttpContext</c> в инфраструктуру значит сделать её незапускаемой без веба
/// (тот же приём, что у <c>INotificationAudience</c>, issue #949).
///
/// ⚠️ Вне запроса — старт приложения, фоновая задача без заказчика, тест — отвечает
/// <see cref="ActivityActor.System" /> (у задачи с заказчиком — её автор, <see cref="ActivityActorScope" />),
/// а не отказывает: запись без автора всё равно полезна, а
/// отказ на старте остановил бы приложение из-за журнала.
/// </summary>
public interface IActivityActor
{
    ActivityActor Current { get; }
}
