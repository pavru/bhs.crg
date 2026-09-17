using BHS.CRG.Application.Settings;

namespace BHS.CRG.Api.Notifications;

/// <summary>
/// Когда мониторингу платить за пробу модели у поставщика (issue #921).
///
/// Раньше расписания не было вовсе: мониторинг звал пробу на каждом круге в 45 секунд, а платил реже
/// только потому, что каталог держал кэш, подогнанный под этот круг. Между расписанием и счётом стоял
/// чужой кэш — до 480 оплаченных запросов генерации в сутки на процесс, и больше всего как раз тогда,
/// когда поставщик отвечает нестабильно. Теперь частотой владеет тот, кто платит, и цена названа
/// здесь.
///
/// Интервалы выведены из того, КАК ЧАСТО поставщики снимают модели с обслуживания — раз в месяцы, — а
/// НЕ из квоты бесплатного тарифа: подгонять интервал под договор пользователя с поставщиком значило
/// бы компенсировать его средствами приложения. Итог — несколько запросов в сутки.
/// </summary>
public static class ModelProbeSchedule
{
    /// <summary>Пока компонент в норме — пересматривать вердикт раз в шесть часов.</summary>
    public static readonly TimeSpan WhileUp = TimeSpan.FromHours(6);

    /// <summary>
    /// Пока объявлен отказ — раз в час. Без этого круг замкнулся бы: вердикт «модель снята» сам не
    /// протухает, выход из отказа требует удачных проверок, а удачной проверка не станет, пока
    /// вердикт не пересмотрен. Один ложный 404 держал бы «недоступен» до смены модели или ключа.
    /// </summary>
    public static readonly TimeSpan WhileDown = TimeSpan.FromHours(1);

    /// <param name="lastPaidAt">Когда этот процесс последний раз разрешал пробу. <c>null</c> — ещё ни разу.</param>
    /// <param name="configChanged">Сменилась модель или ключ — прежний вердикт к новой конфигурации не относится.</param>
    /// <param name="announcedDown">Объявлен отказ компонента.</param>
    public static ModelProbe Decide(DateTimeOffset? lastPaidAt, bool configChanged, bool announcedDown, DateTimeOffset now)
    {
        // Вердикта может не быть вовсе: первый круг процесса (в том числе после перезапуска, когда
        // восстановлен объявленный отказ) или новая конфигурация. Платим, только если в кэше пусто.
        if (lastPaidAt is null || configChanged) return ModelProbe.IfUnknown;

        var interval = announcedDown ? WhileDown : WhileUp;
        return now - lastPaidAt.Value >= interval ? ModelProbe.Refresh : ModelProbe.CacheOnly;
    }
}
