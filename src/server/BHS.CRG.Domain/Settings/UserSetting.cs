namespace BHS.CRG.Domain.Settings;

/// <summary>
/// Одна настройка одного пользователя (ТЗ CORE-25.3, issue #953): тема оформления, язык, а дальше —
/// рабочие пространства, закреплённые разделы и сохранённые представления.
///
/// До неё всё это жило в браузере (<c>crg-theme</c>, <c>crg.locale</c>) и не переживало смену
/// компьютера: человек настраивал систему заново на каждой машине и не понимал, почему «у меня
/// было иначе».
///
/// Почему строка на ключ, а не колонка на настройку. Состав растёт вместе с работами этапа —
/// пространства, закрепления, представления, — и каждая новая колонка означала бы миграцию ради
/// одного предпочтения. Ключи при этом не произвольные: их объявляет <c>UserSettingKeys</c>, а
/// незнакомый ключ адрес не принимает. Хранилище «что угодно по любому ключу» копило бы мусор от
/// опечаток, и опечатка выглядела бы как удавшаяся запись.
///
/// ⚠️ Значение хранится СТРОКОЙ и сервером не толкуется: тема — это «dark», пространства — JSON.
/// Разбирает его тот, кто настройку объявил. Сервер проверяет ровно две вещи — что ключ объявлен и
/// что значение влезает в заявленную ширину (а для перечислимых — что оно из списка).
/// </summary>
public class UserSetting
{
    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private UserSetting() { }

    /// <summary>Чья настройка. Вместе с <see cref="Key" /> — первичный ключ: пара уникальна.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Объявленный ключ: <c>theme</c>, <c>locale</c>. См. <c>UserSettingKeys</c>.</summary>
    public string Key { get; private set; } = "";

    /// <summary>Значение как его прислал клиент.</summary>
    public string Value { get; private set; } = "";

    /// <summary>Когда настройку записали в последний раз.</summary>
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static UserSetting Create(Guid userId, string key, string value) => new()
    {
        UserId = userId,
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
