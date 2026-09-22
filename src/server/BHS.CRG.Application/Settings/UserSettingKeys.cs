using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Settings;

/// <summary>
/// Объявленная настройка пользователя: ключ, предел длины значения и — если значений конечное
/// число — их список.
/// </summary>
/// <param name="Code">Ключ, под которым настройка лежит в базе и ходит по сети: <c>theme</c>.</param>
/// <param name="MaxLength">Предел длины значения. Не украшение: значение приходит снаружи.</param>
/// <param name="OneOf">Допустимые значения, если их список закрыт. null — проверяется только форма.</param>
/// <param name="Shape">Форма значения, когда список открыт (язык). null — любая строка в пределах длины.</param>
public sealed record UserSettingKey(
    string Code,
    int MaxLength,
    IReadOnlyList<string>? OneOf = null,
    Regex? Shape = null)
{
    public bool Accepts(string value) =>
        value.Length <= MaxLength
        && (OneOf is null || OneOf.Contains(value, StringComparer.Ordinal))
        && (Shape is null || Shape.IsMatch(value));
}

/// <summary>
/// Что сервер хранит за пользователем (ТЗ CORE-25.3, issue #953).
///
/// Каталог существует не ради порядка, а вместо проверки — как у <c>ActivityActions</c>: адрес
/// принимает ТОЛЬКО объявленный ключ. Иначе опечатка (<c>them</c> вместо <c>theme</c>) сохранилась
/// бы успешно и молча, а потом настройка «не приезжала» — отказ, переодетый в удавшуюся запись.
///
/// ⚠️ Здесь объявлено ровно то, у чего УЖЕ есть потребитель. Рабочие пространства (`AUTH-16.1`),
/// закреплённые разделы (`AUTH-16.4`) и сохранённые представления (`CORE-33`) хранятся этим же
/// механизмом и приедут своими работами — ключ заводится вместе с тем, кто его читает. Ключ без
/// потребителя проверить нечем: он выглядит работающим ровно до первого чтения.
/// </summary>
public static class UserSettingKeys
{
    /// <summary>
    /// Тема оформления. Значения те же три, что понимает клиент (<c>themeContext.ts</c>):
    /// «как в системе», светлая, тёмная.
    ///
    /// Список закрыт НАРОЧНО, хотя это и второе место, где он записан. Разойтись они могут одним
    /// способом — у клиента появилась четвёртая тема, — и тогда сервер ответит отказом сразу и
    /// вслух. Обратное (сервер принимает что угодно) дало бы тему, которой нет: клиент не нашёл бы
    /// её в своём списке и показал бы светлую, не сказав ни слова.
    /// </summary>
    public static readonly UserSettingKey Theme = new("theme", 16, OneOf: ["system", "light", "dark"]);

    /// <summary>
    /// Язык форматирования дат и чисел. Здесь список НЕ закрыт: набор языков клиент меняет чаще
    /// (`useLocale.ts`), и переписать его сюда значило бы завести второй реестр языков, который
    /// расходится с первым молча — в сторону отказа на языке, который клиент уже предлагает.
    /// Проверяется форма метки языка (BCP 47) и особое значение «системный».
    /// </summary>
    public static readonly UserSettingKey Locale = new("locale", 35,
        Shape: new Regex("^(system|[A-Za-z]{2,8}(-[A-Za-z0-9]{2,8})*)$", RegexOptions.Compiled));

    public static readonly IReadOnlyList<UserSettingKey> All = [Theme, Locale];

    /// <summary>Объявленный ключ или null — и тогда адрес отвечает отказом, а не записью.</summary>
    public static UserSettingKey? Find(string code) =>
        All.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.Ordinal));
}
