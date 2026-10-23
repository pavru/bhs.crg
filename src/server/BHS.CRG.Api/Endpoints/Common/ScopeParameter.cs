using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Api.Endpoints.Common;

/// <summary>
/// Разбор уровня оси расположения из строки запроса (<c>?scope=Construction</c>) для адресов, которые
/// на неизвестный уровень ОТКАЗЫВАЮТ (ревью PR #1585).
///
/// <para>Принимается только имя уровня, без учёта регистра — как его принимали адреса плана и
/// документов качества: клиент, собравший запрос по образцу соседнего адреса, не должен получать отказ
/// за «construction». <c>Enum.TryParse</c> здесь не годится: он разбирает «1» в Section, «99» — в
/// уровень, которого нет, а «Section, Construction» — в сумму значений, и всё это доезжало бы до
/// обработчика ответом «пусто» вместо отказа.</para>
///
/// <para>Адреса, у которых неизвестный уровень значит «вся система» или «без отбора», разбирают его
/// по-своему нарочно — это другое правило, а не копия этого.</para>
/// </summary>
public static class ScopeParameter
{
    public static bool TryParse(string? value, out CatalogScope scope)
    {
        foreach (var level in Enum.GetValues<CatalogScope>())
            if (string.Equals(level.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                scope = level;
                return true;
            }
        scope = default;
        return false;
    }
}
