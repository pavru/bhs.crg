using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>
/// Разбор локального сметного расчёта (<c>OpenXML_LS</c>) — ТЗ PLAN-7, PLAN-8, PLAN-9.1.
///
/// <para>Итог позиции — тот, что записала программа: нормативных баз в системе нет, и пересчёт «по похожей»
/// настройке дал бы итог, отличный от программы, но помеченный как её. Файл, сохранённый без итогов, —
/// отказ «в файле нет итогов» (досчёт по данным файла — второй PR plan-05), а не пустые итоги.</para>
/// </summary>
public static class LocalEstimateReader
{
    public static ParsedLocalEstimate Read(GrandSmetaFile file)
    {
        file.Expect(GrandSmetaKind.LocalEstimate);
        var validation = GrandSmetaValidation.Run(file);
        return GrandSmetaFile.Read(() =>
        {
            var root = file.Root;
            var chapters = root.Chapters().ToList();
            if (chapters.Count == 0)
                throw GrandSmetaFile.Refuse(GrandSmetaRefusal.DocumentInvalid,
                    "В локальном сметном расчёте нет ни одного раздела — разбирать нечего.");
            var all = chapters.SelectMany(c => c.Elements("Position")).ToList();
            if (all.Count > 0 && all.All(p => p.Element("Itog") is null))
                throw GrandSmetaFile.Refuse(GrandSmetaRefusal.NoTotals,
                    "В файле нет итогов: пересчитайте смету в программе и сохраните с итогами.");

            var coefficients = new Coefficients(root);
            var unparsed = new List<Unparsed>();
            var parsed = new List<LocalEstimateChapter>();
            for (var ci = 0; ci < chapters.Count; ci++)
            {
                var chapter = chapters[ci];
                var caption = chapter.Text("Caption") ?? "";
                var positions = new List<LocalEstimatePosition>();
                var index = 0;
                foreach (var p in chapter.Elements("Position"))
                {
                    index++;
                    var place = $"раздел {ci + 1} «{caption}», позиция {p.Text("Number")} {p.Text("Code")}".TrimEnd();
                    validation.Parse(p, place, positions, unparsed, () => Position(p, index, coefficients));
                }

                var itog = chapter.Element("Itog")?.Elements().FirstOrDefault();
                parsed.Add(new LocalEstimateChapter(ci + 1, caption, Sum(itog, "ChapterTotal"), itog?.Number("TZ"), positions));
            }

            var docItog = root.Element("Itog")?.Elements().FirstOrDefault();
            var totals = new LocalEstimateTotals(
                parsed.All(c => c.Total is not null) ? parsed.Sum(c => c.Total!.Value) : null,
                Sum(docItog, "SmetaTotal"), docItog?.Number("TZ"));

            return new ParsedLocalEstimate(file.Source, Requisites(root), DetectMethod(root), parsed, totals, unparsed);
        });
    }

    /// <summary>Итог раздела или сметы. Строка итога есть, а суммы нет — ноль: нулевую сумму программа не пишет
    /// (раздел РИМ из одних нулевых позиций).</summary>
    private static decimal? Sum(XElement? itog, string dataType) =>
        itog?.Descendants("Itog").FirstOrDefault(i => i.Text("DataType") == dataType) is { } line ? line.Number("PZ") ?? 0m : null;

    /// <summary>
    /// Метод расчёта (ТЗ PLAN-8): БИМ — индексы к позиции (<c>IndexesMode</c> не <c>None</c>); при
    /// <c>None</c> — РИМ, если есть индексы к ресурсам (ГОСР: <c>IndexesRes</c>, <c>ForcedResIdx</c>), иначе РМ.
    /// Без <c>IndexesMode</c> — не определён.
    /// </summary>
    public static CalculationMethod DetectMethod(XElement root)
    {
        var mode = root.Element("Indexes")?.Text("IndexesMode");
        if (mode is null) return CalculationMethod.Unknown;
        if (mode != "None") return CalculationMethod.Bim;
        var resourceIndexes = root.Element("Indexes")!.Elements("IndexesRes").Elements("Index").Any()
            || root.Descendants().Any(e => e.HasOption("ForcedResIdx"));
        return resourceIndexes ? CalculationMethod.Rim : CalculationMethod.Rm;
    }

    private static LocalEstimateRequisites Requisites(XElement root)
    {
        var props = root.Element("Properties");
        var dates = root.Element("DocDates");
        return new LocalEstimateRequisites(
            props?.Text("LocNum"), props?.Text("Description"), props?.Text("Constr"), props?.Text("Object"),
            props?.Text("Stage"), dates?.CreationDate(), dates?.Date("CurrPriceDate"),
            root.Element("Parameters")?.Text("BasePrices"));
    }

    private static LocalEstimatePosition Position(XElement p, int index, Coefficients coefficients)
    {
        var number = p.Text("Number") ?? throw new GrandSmetaFormatException("у позиции нет номера");
        var quantity = Quantity(p);
        var itog = p.Element("Itog")?.Elements().FirstOrDefault()
            ?? throw new GrandSmetaFormatException("у позиции нет итога");
        var total = itog.Descendants("Itog").FirstOrDefault(i => i.Text("DataType") == "TotalWithNP")
            ?? throw new GrandSmetaFormatException("в итоге позиции нет «Всего по позиции»");
        var totalCurrent = total.Number("TotalCurr") ?? 0m;

        var applied = itog.Elements("Itog").Where(i => i.Text("DataType") == "Koefficients").Elements("Itog")
            .Select(i => new PositionCoefficient(i.Text("Code"), i.Text("Caption") ?? ""))
            .Where(c => coefficients.Affects(p, c.Code))
            .ToList();
        var materialFactor = coefficients.MaterialFactor(p, applied);
        // Количество «всего» программа округляет до точности объёма позиции (Quantity/@Precision, по умолчанию 7;
        // схема допускает и «-3» — до тысяч)
        var digits = (int)(p.Element("Quantity")?.Number("Precision") ?? 7m);
        var materials = p.Elements("Resources").Elements("Mat")
            .Where(m => !m.HasOption("Deleted", "Attribs"))
            .Select(m => new PositionMaterial(m.Text("Code"), m.Text("Caption") ?? "", m.Text("Units"),
                GrandSmetaXml.Round((m.Number("Quantity") ?? 0m) * materialFactor * quantity, digits)))
            .ToList();

        return new LocalEstimatePosition(
            index, number, p.Text("Code"), p.Text("Caption") ?? "", p.Text("Units"), quantity,
            totalCurrent, total.Number("TotalBase"), TotalSource.Program,
            quantity != 0 ? Math.Round(totalCurrent / quantity, 2, MidpointRounding.AwayFromZero) : null,
            p.HasOption("Inactive"), p.Text("SlaveRow") == "Yes", Labor(itog), materials, applied,
            MarketAnalysis(p));
    }

    /// <summary>Объём — <c>Quantity/@Result</c>, в единицах расценки («100 м» → 0,7). Нулевой результат программа
    /// не пишет, как и нулевую сумму в итоге (<c>TotalCurr</c>): позиция с итогом и без <c>Result</c> — объём 0,
    /// так её показывает и печатная форма (ЛСР ЭОМ, 33.1; «Контроль гофры»).</summary>
    private static decimal Quantity(XElement p) =>
        (p.Element("Quantity") ?? throw new GrandSmetaFormatException("у позиции нет объёма")).Number("Result") ?? 0m;

    /// <summary>Затраты труда рабочих «всего», чел.-ч, с коэффициентами — как в итоге позиции: при БИМ строка
    /// <c>TZ</c>, при РИМ — ресурсы-трудозатраты под статьёй ОТ.</summary>
    private static decimal? Labor(XElement itog)
    {
        var lines = itog.Descendants("Itog").ToList();
        if (lines.FirstOrDefault(i => i.Text("DataType") == "TZ") is { } tz) return tz.Number("Total");
        var ot = lines.FindIndex(i => i.Text("DataType") == "OT");
        if (ot < 0) return null;
        var res = lines.Skip(ot + 1).TakeWhile(i => i.Text("DataType") == "Res").ToList();
        return res.Count > 0 ? res.Sum(i => i.Number("QuantityTotal") ?? 0m) : null;
    }

    private static MarketAnalysisLine? MarketAnalysis(XElement p)
    {
        var link = p.Element("MarketAnalysisDocLink");
        if (link is null) return null;
        var supplier = link.Element("Supplier");
        return new MarketAnalysisLine(
            p.Element("PriceCurr")?.Money("MT"), supplier?.Text("Caption"), supplier?.Text("INN"), supplier?.Text("KPP"),
            link.Money("OptPrice"), link.Money("OptPriceWithVAT"), link.Number("VAT"), link.Date("SellingDate"),
            link.Text("Link"));
    }

    /// <summary>Коэффициенты документа и позиции по шифру: какой действует на позицию и каков коэффициент к расходу
    /// материалов (<c>MatQty</c>, значение <c>Value_MT</c>) — количество «всего» в файле не записано.</summary>
    private sealed class Coefficients(XElement root)
    {
        private static readonly (string Value, string Article, string Resource)[] Articles =
            [("Value_OZ", "OZ", "Tzr"), ("Value_EM", "EM", "Mch"), ("Value_ZM", "ZM", "Tzm"), ("Value_MT", "MT", "Mat")];

        /// <summary>Коэффициенты документа по шифру — первый с шифром, как при переборе.</summary>
        private readonly Dictionary<string, XElement> _document = root.Elements("Koefficients").Elements("K")
            .Where(k => k.Text("Code") is not null)
            .GroupBy(k => k.Text("Code")!)
            .ToDictionary(g => g.Key, g => g.First());

        /// <summary>
        /// Действует ли коэффициент на позицию. Итог позиции перечисляет коэффициенты по привязке к виду работ и
        /// разделу — и те, которым умножать нечего: коэффициент к ОЗП и ЭМ у строки материала. Печатная форма
        /// программы их не показывает, не показывает и разбор: действует коэффициент ко всем статьям
        /// (<c>Value_PZ</c>) или к статье, которая у позиции есть. Не найденный по шифру — действует.
        /// </summary>
        public bool Affects(XElement position, string? code)
        {
            var k = Find(position, code);
            if (k is null || k.Attribute("Value_PZ") is not null) return true;
            var price = position.Element("PriceBase");
            var current = position.Element("PriceCurr");
            var resources = position.Element("Resources");
            return Articles.Any(a => k.Attribute(a.Value) is not null
                && ((price?.Number(a.Article) ?? 0m) != 0 || (current?.Number(a.Article) ?? 0m) != 0
                    || resources?.Element(a.Resource) is not null));
        }

        public decimal MaterialFactor(XElement position, IEnumerable<PositionCoefficient> applied)
        {
            var factor = 1m;
            foreach (var code in applied.Select(a => a.Code).Where(c => c is not null).Distinct())
            {
                var k = Find(position, code);
                if (k is not null && k.HasOption("MatQty"))
                    factor *= k.Number("Value_MT") ?? 1m;
            }

            return factor;
        }

        private XElement? Find(XElement position, string? code) =>
            code is null ? null
                : position.Elements("Koefficients").Elements("K").FirstOrDefault(e => e.Text("Code") == code)
                  ?? _document.GetValueOrDefault(code);
    }
}
