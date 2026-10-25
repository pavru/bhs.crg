using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta.Totals;

/// <summary>
/// Досчёт БИМ (<c>IndexesMode=PosOne</c>, <c>IndexesLinkMode=Category</c>): статья = базисная цена единицы ×
/// коэффициенты × объём, до копейки, × индекс к позиции — по <c>IndexCode</c> или по категории вида работ, тоже
/// до копейки. НР и СП — от ФОТ (ОТ + ОТм) по нормативу вида работ с понижающими <c>NKI</c> и <c>PKI</c>.
/// </summary>
internal sealed class BimTotals(XElement root) : PositionTotals(root)
{
    private readonly Dictionary<string, XElement> _indexes = root.Elements("Indexes").Elements("IndexesPos").Elements("Index")
        .Where(i => i.Text("Code") is not null)
        .GroupBy(i => i.Text("Code")!)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Индекс по категории вида работ: атрибут — категория («Mounting»), значение — код индекса.</summary>
    private readonly XElement? _byCategory = root.Element("Indexes")?.Element("CategoryIndexes");

    protected override decimal Total(XElement position, XElement? vidRab, Factors f, decimal quantity)
    {
        // Строка в текущих ценах (материал по КА, ФСБЦ): цена × коэффициент к материалам × объём, без НР и СП
        if (position.Text("PriceLevel") == "Curr")
        {
            var current = position.Element("PriceCurr");
            var price = (current?.Number("OZ") ?? 0m) + (current?.Number("EM") ?? 0m) + (current?.Number("MT") ?? 0m);
            return Kopecks(price * f.Mt * quantity);
        }

        // Неактивную позицию программа при БИМ обнуляет (ЛСР ОС,СКУД, 13)
        if (position.HasOption("Inactive")) return 0m;
        var priceBase = position.Element("PriceBase");
        if (priceBase is null) return 0m;

        var mt = priceBase.Number("MT") ?? 0m;
        // Удалённый сметчиком ресурс в базисной цене расценки ещё сидит — вычесть
        foreach (var m in position.Elements("Resources").Elements("Mat").Where(m => m.HasOption("Deleted", "Attribs")))
            mt -= (m.Number("Quantity") ?? 0m) * (m.Element("PriceBase")?.Number("Value") ?? 0m);

        var index = Index(position, vidRab);
        decimal Article(decimal basePrice, decimal factor, string article) =>
            Kopecks(Kopecks(basePrice * factor * quantity) * (index?.Number(article) ?? index?.Number("SMR") ?? 1m));

        var oz = Article(priceBase.Number("OZ") ?? 0m, f.Oz, "OZ");
        var em = Article(priceBase.Number("EM") ?? 0m, f.Em, "EM");
        var zm = Article(priceBase.Number("ZM") ?? 0m, f.Zm, "ZM");
        var mat = Article(mt, f.Mt, "MT");
        var fot = oz + zm;
        return oz + em + mat + Kopecks(fot * Rate(vidRab, "Nacl", "NKI") / 100) + Kopecks(fot * Rate(vidRab, "Plan", "PKI") / 100);
    }

    /// <summary>Индекс позиции: свой (<c>IndexCode</c>) или по категории её вида работ. Нет ни того, ни другого —
    /// без индекса; назван, а в документе его нет — позиция не досчитывается.</summary>
    private XElement? Index(XElement position, XElement? vidRab)
    {
        var code = position.Text("IndexCode")
            ?? (vidRab?.Text("Category") is { } category ? _byCategory?.Text(category) : null);
        if (code is null) return null;
        return _indexes.TryGetValue(code, out var index) ? index : throw new GrandSmetaFormatException($"индекс «{code}» не найден в документе");
    }
}
