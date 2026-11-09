namespace BHS.CRG.Modules;

/// <summary>
/// Доступен ли пользователю модуль: у него есть хотя бы одно право этого модуля, не считая прав
/// контракта (ТЗ AUTH-8.1, change <c>plan-21-contract-permissions</c>).
///
/// Правило живёт ЗДЕСЬ, а не в двух местах, потому что спрашивают его многие: ворота адресов
/// (<see cref="ModuleAccessHandler" />), ответ <c>/api/account/access</c>, по которому клиент
/// строит навигацию, аудитория уведомлений, журнал действий и параметр доступа наборов. Разойдясь,
/// они дали бы худший из возможных ответов — пункт меню, который виден и отвечает отказом, или
/// раздел, к которому доступ есть, а войти в него неоткуда.
///
/// <para><b>Почему со справочником, а не по одному коду.</b> Право контракта
/// (<see cref="AppPermission.Contract" />) модуль выдаёт ДРУГИМ модулям: «Монтажнику» — право сметы
/// для подсказки материалов, «Снабженцу» — для заявок. Начало кода у него то же, что у прав самого
/// модуля, и узнать его по коду нельзя. Прежний вид без справочника удалён, а не оставлен рядом:
/// он молча считал бы такое право открывающим — и у «Монтажника» появилось бы пустое
/// «Планирование».</para>
/// </summary>
public static class ModuleAccess
{
    public static bool IsOpen(string moduleCode, IEnumerable<string> granted, PermissionCatalog catalog)
    {
        var prefix = moduleCode + ".";
        return granted.Any(code =>
            code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !catalog.IsContract(code));
    }
}
