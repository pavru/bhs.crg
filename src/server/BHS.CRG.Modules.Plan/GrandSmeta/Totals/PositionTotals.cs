using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta.Totals;

/// <summary>Досчитанная позиция: «Всего по позиции» в текущих ценах, затраты труда рабочих «всего» и
/// коэффициенты, которые на неё действуют, — документа и самой позиции.</summary>
internal sealed record ComputedPosition(decimal Total, decimal Labor, IReadOnlyList<XElement> Coefficients);

/// <summary>
/// Досчёт итога позиции ЛСР, сохранённой без итогов (ТЗ PLAN-8, plan-05 группа 3): только по данным файла —
/// нормативных баз в системе нет. Цель — совпасть с программой сметчика до копейки, а не с Методикой: так же
/// округляется каждая статья, так же считаются вспомогательные ненормируемые (см. <see cref="RimTotals" />).
///
/// <para>Поддерживается <b>закрытый перечень</b> настроек — встреченных в парах «с итогом / без итогов» от
/// владельца (<c>TestData/GrandSmeta</c>): БИМ <c>PosOne</c> + <c>Category</c>, РИМ <c>None</c> + <c>ZpmByTz</c>,
/// обе — <c>MultPosK</c> и <c>2020NoRound</c>, параметры расчёта <see cref="Parameters" />. Метод, опция или
/// параметр вне перечня — отказ документа с названием; опция коэффициента вне перечня — отказ позиции. Итог «по
/// похожей» настройке разошёлся бы с программой, а выглядел бы как её. Новая настройка — с новой парой.</para>
/// </summary>
internal abstract class PositionTotals
{
    /// <summary><c>Parameters/@Options</c> образцов БИМ. Опции, которые на итог позиции не влияют
    /// (<c>AddZatr</c>, <c>2020Mode</c>, <c>SeparateFot</c>, <c>2022Mode</c>), в перечне — потому что встречены:
    /// незнакомая опция — повод остановиться, а не пропустить её.</summary>
    private static readonly HashSet<string> BimOptions = ["AddZatr", "2020Mode", "SeparateFot", "MultPosK", "2022Mode", "2020NoRound"];

    /// <summary>То же у РИМ: ещё <c>ZpmByTz</c> (ОТм по затратам труда машинистов) и <c>2022AuxMatK</c>.</summary>
    private static readonly HashSet<string> RimOptions = [.. BimOptions, "ZpmByTz", "2022AuxMatK"];

    /// <summary>Параметры расчёта <c>Parameters</c> — у всех пар одни: порядок перемножения коэффициентов,
    /// округление расхода, разрядность. Другое значение — отказ: при нём программа считает иначе. Нет атрибута —
    /// значение по умолчанию из схемы, а где его схема не называет — тоже отказ.</summary>
    private static readonly (string Name, string Value, string? Default)[] Parameters =
    [
        ("MultKPosCalcMode", "UseTotalMult", null), ("MatRoundMode", "None", null), ("BlockRoundMode", "Smart", null),
        ("PosKDigits", "7", null), ("MatDigits", "4", null), ("TzDigits", "2", "2"),
    ];

    /// <summary><c>K/@Options</c> коэффициентов пар. <c>Base</c> и <c>Curr</c> обязательны оба: коэффициент только к
    /// базисным или только к текущим ценам здесь применялся бы ко всем.</summary>
    private static readonly HashSet<string> CoefficientOptions =
        ["InPos", "Base", "Curr", "EmAll", "EmQty", "OzpTz", "ZpmTz", "MatQty", "AddOn10", "Percent", "PzAll"];

    private readonly List<DocumentCoefficient> _document;

    /// <summary>Виды работ по коду — с группой, к которой коэффициент документа привязывается тоже.</summary>
    private readonly Dictionary<string, (XElement Vr, string? Group)> _vidRab;

    protected PositionTotals(XElement root)
    {
        _document = root.Elements("Koefficients").Elements("K").Where(k => !k.HasOption("Disabled"))
            .Select(k => new DocumentCoefficient(k)).ToList();
        _vidRab = root.Descendants("VidRab_Group")
            .SelectMany(g => g.Descendants("Vid_Rab").Select(v => (Vr: v, Group: g.Text("ID"))))
            .Where(x => x.Vr.Text("ID") is not null)
            .GroupBy(x => x.Vr.Text("ID")!)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Досчёт для сметы без итогов — или отказ «досчёт не поддерживается» с названием метода и опции.</summary>
    public static PositionTotals For(XElement root, CalculationMethod method)
    {
        if (Unsupported(root, method) is { } what)
            throw GrandSmetaFile.Refuse(GrandSmetaRefusal.NoTotals,
                $"Смета без итогов, {what}: досчёт не поддерживается — обратитесь к разработчику. "
                + "Или пересчитайте смету в программе и сохраните с итогами.");
        return method == CalculationMethod.Bim ? new BimTotals(root) : new RimTotals(root);
    }

    /// <summary>Чего нет в перечне — «метод РМ», «метод БИМ (опция X)»; всё знакомо — null.</summary>
    private static string? Unsupported(XElement root, CalculationMethod method)
    {
        var name = method switch
        {
            CalculationMethod.Bim => "БИМ",
            CalculationMethod.Rim => "РИМ",
            CalculationMethod.Rm => "РМ",
            _ => "не определён",
        };
        if (method is not (CalculationMethod.Bim or CalculationMethod.Rim)) return $"метод {name}";

        var indexes = root.Element("Indexes");
        if (method == CalculationMethod.Bim
            && (indexes?.Text("IndexesMode"), indexes?.Text("IndexesLinkMode")) is not ("PosOne", "Category"))
            return $"метод {name} (индексы {indexes?.Text("IndexesMode")}, привязка {indexes?.Text("IndexesLinkMode")})";

        var parameters = root.Element("Parameters");
        var options = parameters?.Text("Options")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        string[] required = method == CalculationMethod.Rim ? ["MultPosK", "2020NoRound", "ZpmByTz"] : ["MultPosK", "2020NoRound"];
        if (required.FirstOrDefault(o => !options.Contains(o)) is { } missing) return $"метод {name} (нет опции {missing})";
        var known = method == CalculationMethod.Rim ? RimOptions : BimOptions;
        if (options.FirstOrDefault(o => !known.Contains(o)) is { } unknown) return $"метод {name} (опция {unknown})";
        foreach (var (parameter, value, byDefault) in Parameters)
            if ((parameters?.Text(parameter) ?? byDefault) is var actual && actual != value)
                return $"метод {name} (параметр {parameter}={actual ?? "не указан"})";
        return null;
    }

    public ComputedPosition Compute(XElement position, XElement chapter)
    {
        var vidRab = VidRab(position);
        var chapterId = chapter.Text("SysID");
        // Порядок — документа, затем позиции: у программы в итоге он свой и из файла не выводится (ЛСР ЭОМ)
        var applied = _document.Where(k => k.Applies(vidRab, chapterId)).Select(k => k.K)
            .Concat(position.Elements("Koefficients").Elements("K").Where(k => !k.HasOption("Disabled")))
            .ToList();
        foreach (var k in applied) Supported(k);
        var factors = Factors.Of(applied);
        var quantity = (position.Element("Quantity") ?? throw new GrandSmetaFormatException("у позиции нет объёма"))
            .Number("Result") ?? 0m;
        return new ComputedPosition(Total(position, vidRab?.Vr, factors, quantity), Labor(position, applied, quantity), applied);
    }

    /// <summary>Коэффициент, который на позицию действует, — из перечня, иначе позиция не досчитывается.</summary>
    private static void Supported(XElement k)
    {
        var options = k.Text("Options")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var what = options.FirstOrDefault(o => !CoefficientOptions.Contains(o)) is { } unknown ? $"опция {unknown}"
            : !options.Contains("Base") || !options.Contains("Curr") ? "действует не на базисные и текущие цены вместе"
            // Затраты труда берут только коэффициент к ОЗП: к ним ли и «ко всем статьям» — по парам не видно
            : options.Contains("OzpTz") && k.Attribute("Value_PZ") is not null ? "коэффициент ко всем статьям с OzpTz"
            : null;
        if (what is not null)
            throw new GrandSmetaFormatException($"коэффициент «{k.Text("Code")}»: {what} — досчёт не поддерживается");
    }

    protected abstract decimal Total(XElement position, XElement? vidRab, Factors factors, decimal quantity);

    /// <summary>Затраты труда рабочих «всего»: норма × объём × коэффициенты к ОЗП тех, что действуют и на
    /// трудозатраты (<c>OzpTz</c>, «ТЗ=…» в названии), до точности объёма позиции — так их пишет программа в итоге
    /// позиции (ЛСР СКС, 106: 1,3477794, а не 1,34777944).</summary>
    private static decimal Labor(XElement position, IEnumerable<XElement> applied, decimal quantity)
    {
        var factor = applied.Where(k => k.HasOption("OzpTz")).Aggregate(1m, (f, k) => f * (k.Number("Value_OZ") ?? 1m));
        var digits = (int)(position.Element("Quantity")?.Number("Precision") ?? 7m);
        return GrandSmetaXml.Round(position.Elements("Resources").Elements("Tzr").Where(Counted)
            .Sum(r => (r.Number("Quantity") ?? 0m) * quantity * factor), digits);
    }

    /// <summary>Ресурс входит в счёт: сметчик его не удалил и не исключил из итога.</summary>
    protected static bool Counted(XElement resource) =>
        !resource.HasOption("Deleted", "Attribs") && !resource.HasOption("NotCount");

    /// <summary>Норматив НР или СП вида работ, %: текущий, иначе базисный, × понижающий (812/пр, 774/пр).</summary>
    protected static decimal Rate(XElement? vidRab, string rate, string reducing) =>
        vidRab is null ? 0m
            : (vidRab.Number(rate + "Curr") ?? vidRab.Number(rate) ?? 0m) * (vidRab.Number(reducing) ?? 1m);

    protected static decimal Kopecks(decimal value) => GrandSmetaXml.Kopecks(value);

    /// <summary>Вид работ позиции (<c>Vr2001</c>). Назван, а в документе его нет — позиция не досчитывается:
    /// без него нет ни НР и СП, ни привязки коэффициентов.</summary>
    private (XElement Vr, string? Group)? VidRab(XElement position)
    {
        var id = position.Text("Vr2001");
        if (id is null) return null;
        return _vidRab.TryGetValue(id, out var v) ? v : throw new GrandSmetaFormatException($"вид работ {id} не найден в документе");
    }

    /// <summary>Коэффициент документа с разобранными один раз привязками: к видам работ, их группам и разделам.</summary>
    private sealed class DocumentCoefficient(XElement k)
    {
        private readonly bool _allWorks = k.Text("AllVidRabs") == "Yes";
        private readonly bool _allChapters = k.Text("AllChapters") != "No";
        private readonly HashSet<string> _works = Ids(k.Text("VrsLinks"));
        private readonly HashSet<string> _groups = Ids(k.Text("VrGroupsLinks"));
        private readonly HashSet<string> _chapters = Ids(k.Text("ChaptersLinks"));

        public XElement K { get; } = k;

        /// <summary>Действует на позицию, если совпали и вид работ (сам, его группа или «все виды»), и раздел («все
        /// разделы» или раздел в перечне; в парах РИМ — коэффициент 557/пр к двум разделам).</summary>
        public bool Applies((XElement Vr, string? Group)? vidRab, string? chapter)
        {
            var byWork = _allWorks
                || (vidRab is { } v && (_works.Contains(v.Vr.Text("ID")!) || (v.Group is not null && _groups.Contains(v.Group))));
            return byWork && (_allChapters || (chapter is not null && _chapters.Contains(chapter)));
        }

        /// <summary>Перечень кодов «(10057, 10060)».</summary>
        private static HashSet<string> Ids(string? list) =>
            (list ?? "").Trim('(', ')').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }
}

/// <summary>Коэффициенты к статьям — перемножены (<c>MultPosK</c>). <c>Value_PZ</c> — ко всем статьям,
/// <c>EmAll</c> — коэффициент ЭМ и к ОТм, если своего к ОТм нет.</summary>
internal readonly record struct Factors(decimal Oz, decimal Em, decimal Zm, decimal Mt)
{
    public static Factors Of(IEnumerable<XElement> coefficients)
    {
        var f = new Factors(1m, 1m, 1m, 1m);
        foreach (var k in coefficients)
        {
            var all = k.Number("Value_PZ") ?? 1m;
            var em = k.Number("Value_EM");
            var zm = k.Number("Value_ZM") ?? (k.HasOption("EmAll") ? em : null);
            f = new Factors(f.Oz * all * (k.Number("Value_OZ") ?? 1m), f.Em * all * (em ?? 1m), f.Zm * all * (zm ?? 1m),
                f.Mt * all * (k.Number("Value_MT") ?? 1m));
        }

        return f;
    }
}
