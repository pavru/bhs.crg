using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Объекты разноски счёта и его ДОЛЯ на названные отбором объекты (ТЗ CORE-33, COST-20.1; задача G1c,
/// issue #1090).
///
/// <para><b>Долю считает та же арифметика, что и счёт</b> (<see cref="AllocationMath" />), в памяти, а
/// не пропорцией в запросе; читает её общий читатель денег счетов (<see cref="InvoiceMoney" />). Пропорция в базе разошлась бы с ней на копейки округления и на расхождение
/// с суммой к оплате, которые уходят в последнюю часть (ТЗ COST-13), — и итог реестра по стройке не
/// сошёлся бы со счётом, открытым рядом. Цена — строки и части счетов отбора читаются целиком; у
/// итога это ВЕСЬ отбор, а не страница. Отбор по объекту при этом уже сузил счета до одной стройки.
/// ⚠️ Отбор по учётному периоду (<see cref="InvoicePeriods" />) платит ту же цену и так не сужает:
/// «период содержит 2026» — оплаченные счета года целиком на каждое чтение с итогом по «Сумме».
/// Цена известна и принята до «Затрат по стройке» (G5, issue #1098), где деньги месяца понадобятся
/// свёрткой.</para>
/// </summary>
internal sealed class InvoiceShares(IReadOnlyDictionary<Guid, string> labels)
{
    /// <summary>Названия объектов по ссылкам — стройки и статьи вне строек одним списком.</summary>
    public IReadOnlyDictionary<Guid, string> Labels => labels;

    /// <summary>Как названа цель, которой больше нет, — стройку удалили в ядре, статью — в справочнике.</summary>
    public const string Lost = "объект удалён";

    /// <summary>Порядок объектов по названию — один на колонку «Объект» и на расшифровку строки.</summary>
    internal static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true);

    /// <summary>Объект части: стройка или статья вне строек — ровно одно из двух (держит база).</summary>
    public string Label(InvoiceAllocation part) =>
        (part.ConstructionId ?? part.ArticleId) is { } key && labels.TryGetValue(key, out var label) ? label : Lost;

    /// <summary>Объекты счетов страницы — названиями по алфавиту, каждый по разу.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Objects(IEnumerable<InvoiceAllocation> parts) =>
        parts.GroupBy(p => p.InvoiceId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(Label).Distinct(StringComparer.Ordinal).Order(ByName)]);

    /// <summary>
    /// Подпись колонки суммы под отбором по объекту: «доля: Комарова 36». Объекты — те, чьё название
    /// подошло под условия отбора; длинный перечень заменяется числом — подпись стоит в заголовке.
    /// </summary>
    public string Note(IReadOnlyList<TableFilterCondition> naming)
    {
        var named = labels.Values.Append(Lost).Distinct(StringComparer.Ordinal)
            .Where(label => naming.Any(c => c.Matches(label)))
            .Order(ByName).ToList();

        return named.Count switch
        {
            0 => "доля: названных объектов нет",
            <= 3 => $"доля: {string.Join("; ", named)}",
            _ => $"доля: объектов — {named.Count}",
        };
    }

    /// <summary>Итог по долям — тот же, что считал бы запрос по числовой колонке.</summary>
    public static TableTotal Total(IEnumerable<decimal?> shares)
    {
        var known = shares.Where(s => s is not null).Select(s => s!.Value).ToList();
        return known.Count == 0 ? new(0, 0) : new(known.Count, 0, known.Sum(), known.Min(), known.Max());
    }
}
