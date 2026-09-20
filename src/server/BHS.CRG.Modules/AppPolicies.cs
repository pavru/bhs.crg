namespace BHS.CRG.Modules;

/// <summary>
/// Имена политик авторизации (ТЗ AUTH-8): <c>perm:&lt;право&gt;</c> и <c>module:&lt;код&gt;</c>.
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

    /// <summary>Политика «есть право».</summary>
    public static string Permission(string code) => PermissionPrefix + code;

    /// <summary>Политика «есть доступ к модулю».</summary>
    public static string Module(string code) => ModulePrefix + code;
}
