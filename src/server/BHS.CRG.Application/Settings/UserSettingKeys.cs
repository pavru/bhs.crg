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
/// <param name="Valid">Проверка значения, которую регулярным выражением не записать (список своих
/// рабочих пространств — JSON). null — хватает остального.</param>
public sealed record UserSettingKey(
    string Code,
    int MaxLength,
    IReadOnlyList<string>? OneOf = null,
    Regex? Shape = null,
    Func<string, bool>? Valid = null)
{
    public bool Accepts(string value) =>
        value.Length <= MaxLength
        && (OneOf is null || OneOf.Contains(value, StringComparer.Ordinal))
        && (Shape is null || Shape.IsMatch(value))
        && (Valid is null || Valid(value));
}

/// <summary>
/// Что сервер хранит за пользователем (ТЗ CORE-25.3, issue #953).
///
/// Каталог существует не ради порядка, а вместо проверки — как у <c>ActivityActions</c>: адрес
/// принимает ТОЛЬКО объявленный ключ. Иначе опечатка (<c>them</c> вместо <c>theme</c>) сохранилась
/// бы успешно и молча, а потом настройка «не приезжала» — отказ, переодетый в удавшуюся запись.
///
/// ⚠️ Здесь объявлено ровно то, у чего УЖЕ есть потребитель. Рабочие пространства (`AUTH-16.1`)
/// приехали с переключателем (issue #1001); закреплённые разделы (`AUTH-16.4`) хранятся этим же
/// механизмом и приедут своей работой — ключ заводится вместе с тем, кто его читает. Ключ без потребителя проверить нечем: он выглядит
/// работающим ровно до первого чтения.
///
/// Сохранённые представления таблиц (`CORE-33`) сюда НЕ легли, хотя ТЗ это обещало (issue #1095):
/// общее представление не принадлежит тому, кто его читает, — у них своя таблица, <c>table_views</c>.
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

    /// <summary>
    /// Свои рабочие пространства (ТЗ AUTH-16.1, AUTH-16.5; issue #1001) — JSON-список
    /// <c>{ id, name, modules }</c>. Только личные: общие и назначение роли отложены (STG-14).
    /// </summary>
    public static readonly UserSettingKey Workspaces = new("workspaces", 16_000,
        Valid: WorkspaceSettingValues.IsOwnList);

    /// <summary>
    /// Пространство, выбранное последним (AUTH-16.1): в него человек возвращается после входа, если
    /// умолчание не задано. Вычисляемые системные — тоже кодом (<c>all</c>, <c>module:costs</c>).
    /// </summary>
    public static readonly UserSettingKey Workspace = new("workspace", 64, Shape: WorkspaceSettingValues.Choice);

    /// <summary>
    /// Пространство по умолчанию (AUTH-16.5): открывается при входе. Ключа нет — «открывать последнее
    /// выбранное»; так умолчание и снимается.
    /// </summary>
    public static readonly UserSettingKey DefaultWorkspace = new("workspace.default", 64,
        Shape: WorkspaceSettingValues.Choice);

    /// <summary>
    /// Избранные стройки (issue #1218): GUID через запятую, не больше двадцати. Сервер его хранит, но
    /// не читает — ни один адрес по нему не отбирает (сторож — <c>FavoritesAreNotApiParameterTests</c>).
    /// </summary>
    public static readonly UserSettingKey FavoriteConstructions = new("favoriteConstructions",
        FavoriteConstructionValues.MaxLength, Valid: FavoriteConstructionValues.IsList);

    /// <summary>
    /// Включён ли фильтр по избранному. Ключа нет — включён: человек, выбравший избранное, ждёт, что
    /// оно работает. Выключают его из шапки, не трогая сам список.
    /// </summary>
    public static readonly UserSettingKey FavoritesFilter = new("favoritesFilter", 3, OneOf: ["on", "off"]);

    /// <summary>
    /// Подпанель раздела показана или скрыта (issue #1429; до неё — свёртка боковой панели в иконки,
    /// #1355, отсюда имя ключа). Ключа нет — решает ширина окна: человек, ничего не выбиравший, видит
    /// подпанель там, где ей хватает места. Список закрыт по той же причине, что у темы: третье
    /// значение клиент не узнал бы и молча показал бы что-то своё.
    /// </summary>
    public static readonly UserSettingKey Rail = new("rail", 16, OneOf: ["expanded", "collapsed"]);

    public static readonly IReadOnlyList<UserSettingKey> All =
        [Theme, Locale, Workspaces, Workspace, DefaultWorkspace, FavoriteConstructions, FavoritesFilter, Rail];

    /// <summary>Объявленный ключ или null — и тогда адрес отвечает отказом, а не записью.</summary>
    public static UserSettingKey? Find(string code) =>
        All.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.Ordinal));
}
