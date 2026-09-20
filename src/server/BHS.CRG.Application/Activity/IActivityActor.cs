namespace BHS.CRG.Application.Activity;

/// <summary>Автор записи журнала: учётная запись или сам экземпляр.</summary>
/// <param name="Id">null — действие сделал не человек: старт приложения, расписание, фоновая служба.</param>
/// <param name="Name">Имя на момент действия; у системы — <see cref="ActivityActor.System" />.</param>
public sealed record ActivityActor(Guid? Id, string Name)
{
    /// <summary>Автор для того, что делает сам экземпляр. Имя записывается словом, а не пустотой:
    /// пустое имя в журнале читается как «не смогли определить», а это другое утверждение.</summary>
    public static readonly ActivityActor System = new(null, "Система");
}

/// <summary>
/// Кто сейчас действует. Отдельной службой потому, что журнал живёт ниже слоя HTTP, а автор —
/// в запросе: тянуть <c>HttpContext</c> в инфраструктуру значит сделать её незапускаемой без веба
/// (тот же приём, что у <c>INotificationAudience</c>, issue #949).
///
/// ⚠️ Вне запроса — старт приложения, фоновая задача, тест — отвечает
/// <see cref="ActivityActor.System" />, а не отказывает: запись без автора всё равно полезна, а
/// отказ на старте остановил бы приложение из-за журнала.
/// </summary>
public interface IActivityActor
{
    ActivityActor Current { get; }
}
