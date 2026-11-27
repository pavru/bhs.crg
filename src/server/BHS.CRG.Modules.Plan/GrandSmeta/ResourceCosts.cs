using System.Xml.Linq;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta.Totals;
using BHS.CRG.Modules.Plan.Import;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Стоимость отдельного материала, а не доля общего итога работы (PLAN-9.1).
/// Читается только непосредственный Position/Itog: ветви выполнения имеют другой объём и цены.</summary>
internal sealed class ResourceCosts(XElement root, CalculationMethod method)
{
    private readonly PositionTotals? _profile = PositionTotals.SupportedProfile(root, method);

    /// <summary>Проверенные профили 2020 с отдельным начислением НДС документа. Одна надпись Curr,
    /// ставка поставщика или отсутствие НДС не подтверждают основу. КА имеет отдельную семантику цены.</summary>
    private readonly bool _net = root.Element("Parameters") is { } parameters
        && parameters.HasOption("2020Mode") && parameters.Text("Mode2020Order") is "2025" or "2026"
        && root.Elements("AddZatrats").Elements("AddZatrGlava").Where(g => g.Text("Glava") == "15")
            .Elements("AddZatr").Any(a => a.Text("Caption") == "НДС"
            && a.HasOption("Percent") && !a.HasOption("Disabled") && a.Text("Level") == "2"
            && Read(a, "Value") is >= 0);

    public LocalEstimatePosition Apply(XElement position, XElement chapter, LocalEstimatePosition parsed, ComputedPosition? computed)
    {
        var source = position.Elements("Resources").Elements("Mat")
            .Where(m => !m.HasOption("Deleted", "Attribs")).ToList();
        var branchName = method switch { CalculationMethod.Bim => "ItogBim", CalculationMethod.Rim => "ItogRes", _ => null };
        var branches = position.Element("Itog")?.Elements().ToList() ?? [];
        var branch = branches.Count == 1 && branches[0].Name.LocalName == branchName ? branches[0] : null;
        var lines = MaterialLines(branch);
        var activeProfile = _profile is not null && (position.Element("Itog") is null || branch is not null);
        // Досчёт только дополнительных ресурсных слагаемых при отсутствии расшифровки.
        // Записанный итог позиции никогда не пересчитывается.
        var computedAmounts = computed?.MaterialAmounts
            ?? (activeProfile && lines.Count == 0 && source.Count > 0 ? Compute(position, chapter) : null);
        var candidates = source.Select((m, index) => (Material: m, Index: index)).Where(x => !x.Material.HasOption("NotCount"))
            .ToDictionary(x => x.Material, x => lines.Where(l => Matches(x.Material, l, parsed, x.Index)).ToList());
        var uses = candidates.Values.SelectMany(c => c).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var materials = parsed.Materials.Select((m, i) =>
        {
            var xml = source[i];
            var matches = candidates.GetValueOrDefault(xml) ?? [];
            var line = matches.Count == 1 && uses[matches[0]] == 1 ? matches[0] : null;
            var price = Read(line, "ForOneCurr");
            // В БИМ наличие справочной PriceCurr ещё не доказывает её применение.
            if (price is null && method == CalculationMethod.Rim && activeProfile)
                price = Read(xml.Element("PriceCurr"), "Value");
            var amount = xml.HasOption("NotCount") ? 0m : Read(line, "TotalCurr");
            if (amount is null && lines.Count == 0)
                amount = computedAmounts?.GetValueOrDefault(xml);
            return m with { Cost = Cost(price, amount, _net && activeProfile, _net && activeProfile) };
        }).ToList();

        ResourceCost? own = null;
        if (EstimatePositionKinds.Of(parsed) == EstimateRowKind.Material && !position.Elements("Resources").Elements().Any())
        {
            var price = position.Text("PriceLevel") == "Curr" ? Read(position.Element("PriceCurr"), "MT") : null;
            // Итог берём только при однозначной активной ветви и явном TotalCurr; null не становится нулём.
            var totals = branch?.Descendants("Itog").Where(i => i.Text("DataType") == "TotalWithNP").ToList() ?? [];
            var amount = totals.Count == 1 ? Read(totals[0], "TotalCurr") : null;
            if (position.Element("Itog") is null && computed is not null && HasOwnCalculationInputs(position))
                amount = computed.Total;
            if (!PureMaterial(position, branch)) amount = null;
            var kaPrice = position.Element("MarketAnalysisDocLink") is not null;
            var profileNet = _net && activeProfile;
            own = Cost(price, amount, kaPrice || profileNet, profileNet);
        }

        return parsed with { Materials = materials, OwnMaterialCost = own };
    }

    private IReadOnlyDictionary<XElement, decimal?>? Compute(XElement position, XElement chapter)
    {
        try { return _profile?.ComputeMaterials(position, chapter); }
        catch (Exception e) when (e is GrandSmetaFormatException or OverflowException) { return null; }
    }

    /// <summary>Досчёт общего итога допускает умолчания нулём, но отдельной стоимости ресурса нужна
    /// явная активная цена. Пустой Resources у РИМ не является профилем самостоятельного материала.</summary>
    private bool HasOwnCalculationInputs(XElement p) => method switch
    {
        CalculationMethod.Rim => p.Element("Resources") is null && Read(p.Element("PriceCurr"), "MT") is not null,
        CalculationMethod.Bim => Read(p.Element(p.Text("PriceLevel") == "Curr" ? "PriceCurr" : "PriceBase"), "MT") is not null,
        _ => false,
    };

    private static ResourceCost Cost(decimal? price, decimal? amount, bool priceNet, bool amountNet) =>
        new(price, amount, price is not null && priceNet ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown,
            amount is not null && amountNet ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown);

    /// <summary>Res только внутри статьи MAT, с сохранением границы соседних статей. Не выбираем
    /// первый одноимённый Res из труда, машин или повторного расчёта.</summary>
    private static List<XElement> MaterialLines(XElement? branch)
    {
        var markers = branch?.Descendants("Itog").Where(i => i.Text("DataType") == "MAT").ToList() ?? [];
        return markers.Count == 1
            ? markers[0].ElementsAfterSelf("Itog").TakeWhile(i => i.Text("DataType") == "Res").ToList()
            : [];
    }

    private static bool Matches(XElement material, XElement line, LocalEstimatePosition parsed, int index)
    {
        if (material.Text("Caption") != line.Text("Caption")) return false;
        if (line.Text("Code") is { } code && code != material.Text("Code")) return false;
        if (line.Text("Units") is { } unit && unit != material.Text("Units")) return false;
        if (Read(line, "QuantityForOne") is { } norm && norm != Read(material, "Quantity")) return false;
        return Read(line, "QuantityTotal") is not { } total || total == parsed.Materials[index].Quantity;
    }

    /// <summary>Собственная строка с единственной материальной статьёй. Любая конкурирующая статья,
    /// ресурс, НР/СП или неизвестная форма цены не даёт перенести весь итог на материал.</summary>
    private static bool PureMaterial(XElement p, XElement? branch)
    {
        if (p.Elements("Resources").Elements().Any() || !ZeroOrAbsent(p, "AuxMat")) return false;
        var prices = p.Elements().Where(e => e.Name.LocalName is "PriceBase" or "PriceCurr").ToList();
        if (!prices.Any(e => Read(e, "MT") is not null)) return false;
        if (prices.Any(e => new[] { "OZ", "EM", "ZM" }.Any(a => !ZeroOrAbsent(e, a))
            || e.Attribute("PZ") is not null && (Read(e, "PZ") is not { } pz || pz != Read(e, "MT")))) return false;
        return branch is null || !branch.Descendants("Itog").Any(i => i.Text("DataType") is not ("TotalWithNP" or "TotalPos" or "MAT")
            && (!ZeroOrAbsent(i, "TotalCurr") || !ZeroOrAbsent(i, "TotalBase")));
    }

    private static bool ZeroOrAbsent(XElement element, string attribute) =>
        element.Attribute(attribute) is null || Read(element, attribute) == 0m;

    /// <summary>Недоказанное ресурсное поле не превращает принятую позицию в неразобранную.
    /// Для формулы, которую данный профиль не поддерживает, сохраняется неизвестное.</summary>
    private static decimal? Read(XElement? element, string attribute)
    {
        try { return element?.Number(attribute); }
        catch (Exception e) when (e is GrandSmetaFormatException or OverflowException) { return null; }
    }
}
