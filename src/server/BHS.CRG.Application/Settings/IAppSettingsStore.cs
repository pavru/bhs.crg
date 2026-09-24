namespace BHS.CRG.Application.Settings;

/// <summary>
/// Настройки экземпляра системы (ТЗ CORE-25.3, issue #960).
///
/// ⚠️ Работает только с объявленными ключами (<see cref="AppSettingKeys" />); проверку делает
/// вызывающий, и она обязана быть отказом, а не молчаливым пропуском записи.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>Значение по ключу или <c>null</c>, если настройку никто не задавал.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Записать значение; <c>null</c> снимает настройку — «вернуть умолчание».</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>
    /// Часовой пояс компании — с умолчанием, а не «пусто». Умолчание — пояс СЕРВЕРА: другого
    /// осмысленного у системы нет, а отказ старта ради ненастроенного пояса остановил бы обновление
    /// у заказчика, которому учёт работ ещё не нужен (ТЗ CORE-29 против половинчатого CORE-31).
    ///
    /// <para>Сервер в контейнере чаще всего стоит в UTC — и это видно в интерфейсе: администратор
    /// поменяет пояс, если он не тот. Молчаливое «Europe/Moscow» выглядело бы выбранным.</para>
    /// </summary>
    Task<TimeZoneInfo> GetCompanyTimeZoneAsync(CancellationToken ct = default);
}
