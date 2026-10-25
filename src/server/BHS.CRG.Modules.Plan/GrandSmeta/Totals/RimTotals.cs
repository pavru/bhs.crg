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
        if (resources is null) return Kopecks(CurrentPrice(position, "MT") * f.Mt * quantity);

        decimal ot = 0, em = 0, otm = 0, mt = 0, otPlain = 0;
        foreach (var r in resources.Elements().Where(Counted))
        {
            var norm = r.Number("Quantity") ?? 0m;
            var price = CurrentPrice(r, "Value");
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
                        // Нулевых значений программа не пишет: нет атрибута — ноль
                        otm += Kopecks(machine * (tzm.Number("Quantity") ?? 0m) * (tzm.Number("Price") ?? 0m));
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

    /// <summary>
    /// Текущая цена ресурса или строки. Записана без значения — ноль: нулевых значений программа не пишет. Не
    /// записана вовсе, а базисная есть — отказ позиции: такую цену программа берёт базисной × индекс к ресурсу
    /// (ГОСР), а этого в парах не было, и досчёт взял бы ноль за настоящую цену.
    /// </summary>
    private static decimal CurrentPrice(XElement owner, string attribute)
    {
        if (owner.Element("PriceCurr") is { } current) return current.Number(attribute) ?? 0m;
        var basePrice = owner.Element("PriceBase");
        if ((basePrice?.Number(attribute) ?? 0m) != 0)
            throw new GrandSmetaFormatException(
                $"у «{owner.Text("Code")}» нет текущей цены: цена по индексу к базисной досчётом не поддерживается");
        return 0m;
    }
}
