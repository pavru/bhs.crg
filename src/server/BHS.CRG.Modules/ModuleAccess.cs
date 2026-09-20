namespace BHS.CRG.Modules;

/// <summary>
/// Доступен ли пользователю модуль: у него есть хотя бы одно право этого модуля (ТЗ AUTH-8.1).
///
/// Правило живёт ЗДЕСЬ, а не в двух местах, потому что спрашивают его двое: ворота адресов
/// (<see cref="ModuleAccessHandler" />) и ответ <c>/api/account/access</c>, по которому клиент
/// строит навигацию. Разойдясь, эти двое дали бы худший из возможных ответов — пункт меню, который
/// виден и отвечает отказом, или раздел, к которому доступ есть, а войти в него неоткуда.
/// </summary>
public static class ModuleAccess
{
    public static bool IsOpen(string moduleCode, IEnumerable<string> granted)
    {
        var prefix = moduleCode + ".";
        return granted.Any(code => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
