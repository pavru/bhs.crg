namespace BHS.CRG.Modules;

/// <summary>
/// Имена политик авторизации (ТЗ AUTH-8): <c>perm:&lt;право&gt;</c>, <c>perm:&lt;а&gt;|&lt;б&gt;</c>
/// («любое из прав») и <c>module:&lt;код&gt;</c>.
///
/// Имена строятся здесь, а не пишутся строками по месту: политика с опечаткой не существует, а
/// несуществующая политика — это отказ приложения на запросе, а не при сборке. Одна точка сборки
/// имени превращает опечатку в ошибку компиляции там, где код называет право константой.
///
/// Политики объявлены в проекте контрактов, потому что ворота ставит ядро модулей
/// (<see cref="AppModuleExtensions.MapAppModules" />), а не приложение: иначе имя политики знал бы
/// один проект, а пользовался бы им другой — и связь держалась бы на совпадении строк.
/// </summary>
public static class AppPolicies
{
    public const string PermissionPrefix = "perm:";
    public const string ModulePrefix = "module:";

    /// <summary>
    /// Разделитель «любого из» в имени политики. В кодах прав его не бывает: код — это
    /// <c>модуль.объект.действие</c> (AUTH-1).
    /// </summary>
    public const char AnySeparator = '|';

    /// <summary>Политика «есть право».</summary>
    public static string Permission(string code) => PermissionPrefix + code;

    /// <summary>
    /// Политика «есть хотя бы одно из прав» (plan-02, AUTH-8). Одна политика, а не две двери: две
    /// двери дали бы адресу два источника, а переписи ворот — две записи о том же.
    ///
    /// Список из одного кода — та же политика, что <see cref="Permission" />: список могут
    /// собирать из вкладов включённых модулей, и при одном вкладе имя не должно отличаться от
    /// обычного. Пустой список — отказ: ворота без права не проверяют ничего, а выглядят как проверка.
    /// </summary>
    public static string AnyPermission(params string[] codes)
    {
        var distinct = codes.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length > 0
            ? PermissionPrefix + string.Join(AnySeparator, distinct)
            : throw new ArgumentException("«Любое из прав» без единого права ничего не проверяет.", nameof(codes));
    }

    /// <summary>Политика «есть доступ к модулю».</summary>
    public static string Module(string code) => ModulePrefix + code;

    /// <summary>
    /// Коды прав политики <c>perm:</c> — один или список «любого из»; <c>null</c>, если политика не
    /// про права. Нужен переписям ворот: разбирать имя строкой по месту значило бы завести вторую
    /// копию его устройства, и составное имя одна из них однажды прочла бы как один код.
    /// </summary>
    public static IReadOnlyList<string>? PermissionCodes(string? policyName) =>
        policyName is not null && policyName.StartsWith(PermissionPrefix, StringComparison.OrdinalIgnoreCase)
            ? [.. policyName[PermissionPrefix.Length..].Split(AnySeparator).Select(c => c.Trim())]
            : null;
}
