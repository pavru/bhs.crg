namespace BHS.CRG.Application.Settings;

/// <summary>
/// Предпочтения пользователя на сервере (ТЗ CORE-25.3, issue #953).
///
/// ⚠️ Хранилище работает ТОЛЬКО с объявленными ключами (<see cref="UserSettingKeys" />): проверку
/// делает вызывающий, и она обязана быть отказом, а не молчаливым пропуском записи.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>Все настройки пользователя. Ключа нет — значит человек его не менял.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Записать набор настроек ОДНИМ сохранением: присланные ключи меняются, остальные остаются как
    /// были, значение null убирает настройку — «вернуть как по умолчанию», и отсутствие строки
    /// означает ровно то же, что её никогда не было.
    ///
    /// ⚠️ Именно набором, а не по одному ключу за вызов: отказ базы на середине списка оставил бы
    /// настройки в состоянии, которого человек не выбирал, и отчитался бы об этом ошибкой.
    /// </summary>
    Task ApplyAsync(Guid userId, IReadOnlyDictionary<string, string?> patch, CancellationToken ct = default);
}
