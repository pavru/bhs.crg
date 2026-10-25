using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta.Totals;

/// <summary>
/// Досчёт РИМ (<c>IndexesMode=None</c>, <c>ZpmByTz</c>): расход ресурса = норма × коэффициент × объём, стоимость =
/// расход × текущая цена, до копейки по каждому ресурсу. ОТм — по затратам труда машинистов (<c>Tzm2022</c>).
/// НР и СП — от ФОТ по нормативу вида работ с понижающими <c>NKR</c> и <c>PKR</c>.
///
/// <para>Вспомогательные ненормируемые (<c>@AuxMat</c>, %) — от ОТ <b>без</b> коэффициентов, хотя опция
/// <c>2022AuxMatK</c> стоит: так считает программа сметчика (пары РИМ), а цель досчёта — совпасть с ней.</para>
/// </summary>
internal sealed class RimTotals(XElement root) : PositionTotals(root)
{
    protected override decimal Total(XElement position, XElement? vidRab, Factors f, decimal quantity)
    {
        var resources = position.Element("Resources");
        // Строка ресурса (ФСБЦ, материал по КА): цена в текущих × коэффициент × объём, без НР и СП
        if (resources is null) return Kopecks((position.Element("PriceCurr")?.Number("MT") ?? 0m) * f.Mt * quantity);

        decimal ot = 0, em = 0, otm = 0, mt = 0, otPlain = 0;
        foreach (var r in resources.Elements().Where(Counted))
        {
            var norm = r.Number("Quantity") ?? 0m;
            var price = r.Element("PriceCurr")?.Number("Value") ?? 0m;
            switch (r.Name.LocalName)
            {
                case "Tzr":
                    ot += Kopecks(norm * f.Oz * quantity * price);
                    otPlain += Kopecks(norm * quantity * price);
                    break;
                case "Mch":
                    var machine = norm * f.Em * quantity;
                    em += Kopecks(machine * price);
                    if (r.Element("Tzm2022") is { } tzm)
                        otm += Kopecks(machine * (tzm.Number("Quantity") ?? 1m) * (tzm.Number("Price") ?? 0m));
                    break;
                case "Mat":
                    mt += Kopecks(norm * f.Mt * quantity * price);
                    break;
            }
        }

        if (position.Number("AuxMat") is { } aux) mt += Kopecks(otPlain * aux / 100);
        var fot = ot + otm;
        return ot + em + otm + mt + Kopecks(fot * Rate(vidRab, "Nacl", "NKR") / 100) + Kopecks(fot * Rate(vidRab, "Plan", "PKR") / 100);
    }
}
