using Microsoft.AspNetCore.DataProtection;

namespace BHS.CRG.Infrastructure.Settings;

/// <summary>
/// Шифрование секретов настроек интеграций (ключи распознавания и поиска, пароль SMTP) для
/// хранения в БД. API их и раньше маскировал при чтении, но в самой строке JSONB они лежали как
/// есть: дамп, реплика или копия базы отдавали их открытым текстом.
/// </summary>
/// <remarks>
/// <para>
/// Зашифрованное значение узнаётся по префиксу <see cref="Prefix"/>. Различать по «получилось ли
/// расшифровать» было бы хуже: сохранённый ранее открытый ключ теоретически может оказаться
/// похожим на полезную нагрузку, а отличать миграцию от порчи данных надо надёжно.
/// </para>
/// <para>
/// ⚠️ Ключи Data Protection становятся ключами и от секретов интеграций. Потеря тома
/// <c>dp_keys</c> теперь означает не только протухшие ссылки сброса пароля, но и то, что ключи
/// интеграций придётся ввести заново (см. docs/DEPLOYMENT.md).
/// </para>
/// </remarks>
public class SettingsSecretProtector(IDataProtectionProvider provider)
{
    /// <summary>Метка зашифрованного значения. Версия в префиксе — чтобы смена схемы была различима.</summary>
    private const string Prefix = "enc:v1:";

    private readonly IDataProtector _protector = provider.CreateProtector("BHS.CRG.IntegrationSettings.v1");

    /// <summary>Значение уже зашифровано? Открытые значения остались от версий до 0.92.0.</summary>
    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Зашифровать. Пустое значение остаётся пустым: «ключ не задан» — это не секрет.</summary>
    public string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return IsProtected(value) ? value : Prefix + _protector.Protect(value);
    }

    /// <summary>
    /// Расшифровать. Значение без метки возвращается как есть — это ещё не перешифрованное
    /// наследство, и ронять на нём распознавание или почту нельзя.
    /// </summary>
    public string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsProtected(value)) return value;
        try
        {
            return _protector.Unprotect(value[Prefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Ключи Data Protection недоступны (том не примонтирован, путь переехал) или сменились.
            // Наружу отдаём пусто: движок честно скажет «ключ не задан» и не пойдёт в сеть с мусором.
            //
            // Важно, что это НЕ теряет секрет: расшифровка вызывается только на границе чтения
            // наружу (BuildEffective), а путь сохранения работает с хранимым видом и записывает
            // шифротекст обратно как есть. Верните том — и значения снова читаются.
            return null;
        }
    }
}
