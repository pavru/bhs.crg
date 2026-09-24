namespace BHS.CRG.Domain.Settings;

/// <summary>
/// Одна настройка ЭКЗЕМПЛЯРА системы — то, что верно для всей компании, а не для человека
/// (ТЗ CORE-25.3, issue #960). Первая такая — часовой пояс компании.
///
/// <para>Устроена как <see cref="UserSetting" />, и нарочно так же: строка на ключ, ключи объявлены
/// кодом (<c>AppSettingKeys</c>), незнакомый ключ адрес не принимает. Колонка на настройку означала
/// бы миграцию ради каждой новой галки, а состав настроек растёт вместе с этапами.</para>
///
/// <para>⚠️ Это НЕ настройки интеграций: те держат секреты, шифруются и живут одним JSON-синглтоном
/// (<c>IntegrationSettingsEntity</c>). Здесь — открытые значения, которые читает код и показывает
/// интерфейс; класть их к секретам значило бы отдавать ключи всем, кому нужен часовой пояс.</para>
/// </summary>
public class AppSetting
{
    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private AppSetting() { }

    /// <summary>Объявленный ключ: <c>company.timezone</c>. См. <c>AppSettingKeys</c>.</summary>
    public string Key { get; private set; } = "";

    /// <summary>Значение как строка; толкует его тот, кто настройку объявил.</summary>
    public string Value { get; private set; } = "";

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static AppSetting Create(string key, string value) => new()
    {
        Key = key,
        Value = value,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public void SetValue(string value)
    {
        Value = value;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
