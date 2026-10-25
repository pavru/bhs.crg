using System.Text;

namespace BHS.CRG.Modules;

/// <summary>
/// Приведение текста к ключу запомненного сопоставления — одно правило на все модули: соответствия поставщиков
/// (<c>costs</c>, <c>SupplierMatchKey</c>) и ресурсы смет (<c>plan</c>, <c>ResourceMatchKey</c>). Одно место, а не
/// копии: ослабь правило в одном модуле и забудь в другом — одинаковые наименования начали бы узнаваться
/// по-разному.
///
/// <para>Регистр, «ё» и пробелы. И только. ⚠️ <b>Осторожно нарочно.</b> «3х2,5» и «3x2.5» (кириллическая и
/// латинская буква, запятая и точка) здесь РАЗНЫЕ ключи: правило, сводящее их, свело бы и «1,5» с «15». Ошибка
/// «не узнал» стоит человеку одного выбора; ошибка «узнал не то» — неверной позиции в каждом следующем
/// документе. Ослаблять правило — отдельным решением и с пересчётом ключей обоих модулей по исходному тексту,
/// который каждый из них хранит.</para>
/// </summary>
public static class MatchKeyText
{
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        var gap = false;
        foreach (var symbol in text.Trim())
        {
            if (char.IsWhiteSpace(symbol))
            {
                gap = true;
                continue;
            }
            if (gap) result.Append(' ');
            gap = false;
            var lower = char.ToLowerInvariant(symbol);
            result.Append(lower == 'ё' ? 'е' : lower);
        }
        return result.ToString();
    }
}
