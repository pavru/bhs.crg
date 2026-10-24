using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Разбор образцов заказчика совпадает с эталонами (plan-05, задачи 2.1–2.3, ТЗ PLAN-7). Эталон снят с
/// <b>печатной формы</b> программы, а не с XML: форма — собственное прочтение файла программой и от разбора не
/// зависит, так что тест ловит и ошибку разбора, и ошибку понимания формата.
///
/// <para>Расхождения собираются списком и показываются все сразу: по одному первому не понять, в чём дело.
/// Эталон не подгоняют под разбор — его пересобирают скриптом с формы.</para>
/// </summary>
public class GrandSmetaEtalonTests
{
    public static TheoryData<string> LsrSamples => new(GrandSmetaSamples.Lsr);
    public static TheoryData<string> VorSamples => new(GrandSmetaSamples.Vor);
    public static TheoryData<string> KaSamples => new(GrandSmetaSamples.Ka);

    [Theory]
    [MemberData(nameof(LsrSamples))]
    public void ЛСР_совпадает_с_эталоном_по_печатной_форме(string sample)
    {
        var e = GrandSmetaSamples.Etalon(sample);
        var m = LocalEstimateReader.Read(GrandSmetaSamples.Open(sample));
        var diff = new Diff();

        Common(diff, e, m.Source, GrandSmetaKind.LocalEstimate);
        var req = e.GetProperty("requisites");
        diff.Eq("номер", req.Str("number"), m.Requisites.Number);
        diff.Eq("наименование", req.Str("name"), m.Requisites.Name);
        diff.Eq("стройка", req.Str("construction"), m.Requisites.Construction);
        diff.Eq("объект", req.Str("object"), m.Requisites.Object);
        var quarter = e.GetProperty("priceQuarter");
        diff.Eq("уровень цен", $"{quarter.Int("year")} кв. {quarter.Int("quarter")}",
            m.Requisites.CurrentPriceDate is { } d ? $"{d.Year} кв. {(d.Month + 2) / 3}" : null);
        diff.Eq("метод", e.Str("method"), m.Method.ToString());
        diff.Unparsed(m.Unparsed);

        // Неактивной позиции в форме нет: печатное — это активные.
        diff.Eq("разделов", e.GetProperty("counts").Int("chapters"), m.Chapters.Count);
        diff.Eq("позиций в форме", e.GetProperty("counts").Int("printedPositions"), m.Positions.Count(p => !p.IsInactive));
        diff.Eq("итог (Σ разделов)", e.GetProperty("total").Dec("current"), m.Totals.Total);
        diff.Eq("ВСЕГО по смете", e.GetProperty("estimateTotal").Dec("current"), m.Totals.EstimateTotal);
        diff.Eq("затраты труда", e.Dec("laborTotal"), m.Totals.Labor);

        var chapters = e.Arr("chapters").ToList();
        for (var i = 0; i < Math.Min(chapters.Count, m.Chapters.Count); i++)
        {
            var (ce, cm) = (chapters[i], m.Chapters[i]);
            var at = $"раздел {i + 1}";
            diff.Eq(at + ": название", ce.Str("caption"), cm.Caption);
            diff.Eq(at + ": позиций", ce.Int("printedPositions"), cm.Positions.Count(p => !p.IsInactive));
            diff.Eq(at + ": Σ «Всего по позиции» формы = итог раздела", ce.Dec("positionsTotalCurrent"), cm.Total);
            diff.Eq(at + ": затраты труда", ce.Dec("labor"), cm.Labor);
        }

        foreach (var pe in e.Arr("positions"))
        {
            var chapter = pe.Int("chapter");
            var at = $"раздел {chapter}, позиция {pe.Str("number")}";
            var pm = m.Chapters.ElementAtOrDefault(chapter - 1)?.Positions.FirstOrDefault(p => p.Number == pe.Str("number"));
            if (pm is null)
            {
                diff.Add(at + ": нет в разборе");
                continue;
            }

            diff.Eq(at + ": шифр", pe.Str("code"), pm.Code);
            diff.Eq(at + ": наименование", pe.Str("caption"), pm.Caption);
            diff.Eq(at + ": единица", pe.Str("units"), pm.Units);
            diff.Eq(at + ": объём", pe.Dec("quantity"), pm.Quantity);
            diff.Eq(at + ": всего по позиции", pe.Dec("totalCurrent"), pm.Total);
            diff.Eq(at + ": всего по позиции, база", pe.Dec("totalBase"), pm.TotalBase);
            diff.Eq(at + ": трудозатраты", pe.Dec("labor"), pm.Labor);
            diff.Eq(at + ": коэффициенты", string.Join(" | ", pe.Arr("coefficients").Select(c => c.GetString()).Order()),
                string.Join(" | ", pm.Coefficients.Select(c => c.Code).Order()));
            var materials = pe.Arr("materials").ToList();
            diff.Eq(at + ": материалов", materials.Count, pm.Materials.Count);
            foreach (var me in materials)
            {
                var mm = pm.Materials.FirstOrDefault(x => x.Code == me.Str("code") && x.Caption == me.Str("caption"));
                if (mm is null) diff.Add($"{at}: нет материала {me.Str("code")}");
                else
                {
                    diff.Eq($"{at}: {me.Str("code")} единица", me.Str("units"), mm.Units);
                    diff.Eq($"{at}: {me.Str("code")} количество всего", me.Dec("quantity"), mm.Quantity);
                }
            }

            if (pe.TryGetProperty("marketAnalysis", out var ka))
            {
                diff.Eq(at + ": строка по КА", true, pm.MarketAnalysis is not null);
                diff.Eq(at + ": сметная цена по КА", ka.Dec("price"), pm.MarketAnalysis?.Price);
                diff.Eq(at + ": ИНН поставщика", ka.Str("inn"), pm.MarketAnalysis?.Inn);
                diff.Eq(at + ": дата КП", ka.Str("date"), pm.MarketAnalysis?.SellingDate?.ToString("dd.MM.yyyy"));
            }
        }

        diff.Assert(sample);
    }

    [Theory]
    [MemberData(nameof(VorSamples))]
    public void ВОР_совпадает_с_эталоном_по_печатной_форме(string sample)
    {
        var e = GrandSmetaSamples.Etalon(sample);
        var m = VolumeStatementReader.Read(GrandSmetaSamples.Open(sample));
        var diff = new Diff();

        Common(diff, e, m.Source, GrandSmetaKind.VolumeStatement);
        var req = e.GetProperty("requisites");
        diff.Eq("номер", req.Str("number"), m.Requisites.Number);
        diff.Eq("стройка", req.Str("construction"), m.Requisites.Construction);
        diff.Eq("объект", req.Str("object"), m.Requisites.Object);
        // «00.00.0000» — форма так печатает незаполненную дату
        var date = req.Str("date") is { } s && s != "00.00.0000" ? s : null;
        diff.Eq("дата составления", date, m.Requisites.CreationDate?.ToString("dd.MM.yyyy"));
        diff.Unparsed(m.Unparsed);
        diff.Eq("разделов", e.GetProperty("counts").Int("chapters"), m.Chapters.Count);
        diff.Eq("работ", e.GetProperty("counts").Int("positions"), m.Works.Count());

        var chapters = e.Arr("chapters").ToList();
        for (var i = 0; i < Math.Min(chapters.Count, m.Chapters.Count); i++)
        {
            var (ce, cm) = (chapters[i], m.Chapters[i]);
            diff.Eq($"раздел {i + 1}: название", ce.Str("caption"), cm.Caption);
            diff.Eq($"раздел {i + 1}: работ", ce.Int("positions"), cm.Works.Count);
            diff.Eq($"раздел {i + 1}: заголовки и примечания", string.Join(" | ", ce.Arr("notes").Select(n => n.GetString())),
                string.Join(" | ", cm.Notes));
        }

        foreach (var pe in e.Arr("positions"))
        {
            var at = $"работа {pe.Str("number")}";
            var w = m.Chapters.ElementAtOrDefault(pe.Int("chapter") - 1)?.Works.FirstOrDefault(x => x.Number == pe.Str("number"));
            if (w is null)
            {
                diff.Add(at + ": нет в разборе");
                continue;
            }

            diff.Eq(at + ": наименование", pe.Str("caption"), w.Caption);
            diff.Eq(at + ": единица", pe.Str("units"), w.Units);
            diff.Eq(at + ": объём", pe.Dec("quantity"), w.Quantity);
            diff.Eq(at + ": доп. информация", pe.Str("comment"), w.Comment2);
            // Графа 6 формы — ссылка на чертежи сметчика и комментарии ссылок на листы
            diff.Eq(at + ": ссылки на чертежи", Set(pe.Arr("references").Select(x => x.GetString())),
                Set(w.Sheets.Select(x => x.Comment).Prepend(w.Comment1)));
            diff.Eq(at + ": файлы", Set(pe.Arr("files").Select(x => x.GetString())), Set(w.Sheets.Select(x => x.FileName)));
            diff.Eq(at + ": страницы", Set(pe.Arr("pages").Select(x => x.GetString())), Set(w.Sheets.Select(x => x.Pages)));
            Formula(diff, at, pe.Str("formula"), w);
        }

        diff.Assert(sample);
    }

    [Theory]
    [MemberData(nameof(KaSamples))]
    public void КА_совпадает_с_эталоном_по_печатной_форме(string sample)
    {
        var e = GrandSmetaSamples.Etalon(sample);
        var m = ConjunctureReader.Read(GrandSmetaSamples.Open(sample));
        var diff = new Diff();

        Common(diff, e, m.Source, GrandSmetaKind.Conjuncture);
        diff.Eq("объект", e.GetProperty("requisites").Str("object"), m.Requisites.Object);
        diff.Unparsed(m.Unparsed);
        var counts = e.GetProperty("counts");
        var offered = m.Positions.Where(p => p.Offers.Count > 0).ToList();
        diff.Eq("разделов", counts.Int("chapters"), m.Chapters.Count);
        diff.Eq("позиций с предложениями", counts.Int("positions"), offered.Count);
        diff.Eq("предложений", counts.Int("offers"), offered.Sum(p => p.Offers.Count));
        diff.Eq("поставщиков в предложениях", counts.Int("suppliers"),
            offered.SelectMany(p => p.Offers).Select(o => (o.Supplier?.Caption, o.Supplier?.Inn)).Distinct().Count());

        var chapters = e.Arr("chapters").ToList();
        for (var i = 0; i < Math.Min(chapters.Count, m.Chapters.Count); i++)
        {
            diff.Eq($"раздел {i + 1}: название", chapters[i].Str("caption"), m.Chapters[i].Caption);
            diff.Eq($"раздел {i + 1}: позиций", chapters[i].Int("positions"), m.Chapters[i].Positions.Count(p => p.Offers.Count > 0));
        }

        foreach (var pe in e.Arr("positions"))
        {
            var at = $"позиция {pe.Str("number")}";
            var p = m.Chapters.ElementAtOrDefault(pe.Int("chapter") - 1)?.Positions.FirstOrDefault(x => x.Number == pe.Str("number"));
            if (p is null)
            {
                diff.Add(at + ": нет в разборе");
                continue;
            }

            diff.Eq(at + ": наименование", pe.Str("caption"), p.Caption);
            diff.Eq(at + ": единица", pe.Str("units"), p.Units);
            diff.Eq(at + ": минимальная отпускная без НДС", pe.Dec("minPriceWithoutVat"), p.MinSellingPrice);
            var offers = pe.Arr("offers").ToList();
            diff.Eq(at + ": предложений", offers.Count, p.Offers.Count);
            foreach (var oe in offers)
            {
                var o = p.Offers.FirstOrDefault(x => x.Number == oe.Str("number"));
                var ot = $"{at}.{oe.Str("number")}";
                if (o is null)
                {
                    diff.Add(ot + ": нет в разборе");
                    continue;
                }

                diff.Eq(ot + ": наименование в КП", oe.Str("caption"), o.Caption.Trim());
                diff.Eq(ot + ": без НДС", oe.Dec("priceWithoutVat"), o.SellingPrice);
                diff.Eq(ot + ": с НДС", oe.Dec("priceWithVat"), o.SellingPriceWithVat);
                diff.Eq(ot + ": поставщик", oe.Str("supplier"), o.Supplier?.Caption);
                diff.Eq(ot + ": ИНН", oe.Str("inn"), o.Supplier?.Inn);
                diff.Eq(ot + ": КПП", oe.Str("kpp"), o.Supplier?.Kpp);
                diff.Eq(ot + ": страна", oe.Str("country"), o.Country);
                var period = oe.Str("month") ?? $"{oe.Int("year")} кв. {oe.Int("quarter")}";
                diff.Eq(ot + ": дата КП", period, o.SellingDate is not { } d ? null
                    : oe.Has("month") ? d.ToString("MM.yyyy") : $"{d.Year} кв. {(d.Month + 2) / 3}");
            }
        }

        diff.Assert(sample);
    }

    private static void Common(Diff diff, JsonElement e, GrandSmetaSource source, GrandSmetaKind kind)
    {
        diff.Eq("вид", e.Str("kind"), kind.ToString());
        diff.Eq("вид в разборе", kind, source.Kind);
        diff.Eq("версия программы", true, source.ProgramVersion.StartsWith(e.Str("programVersion") + ".", StringComparison.Ordinal));
        diff.Eq("редакция схемы", GrandSmetaSchemas.Edition, source.SchemaEdition);
    }

    /// <summary>Графа 5 формы — формула с подставленными значениями переменных («Г1 = 2,7…»), а не текст
    /// сметчика. Поэтому дословно сверяется формула без имён; со строками расчёта — их идентификаторы.</summary>
    private static void Formula(Diff diff, string at, string? printed, VolumeWork w)
    {
        if (w.CalcRows.Count > 0)
        {
            var ids = (printed ?? "").Split('\n').Select(l => l.Split(" = ")[0].Trim());
            diff.Eq(at + ": строки расчёта", Set(ids), Set(w.CalcRows.Select(r => r.Identifier)));
            return;
        }

        var formula = Norm(w.Formula);
        if (formula is null || Regex.IsMatch(formula, @"\p{L}")) return;
        // Голое число форма не печатает: оно и есть объём
        diff.Eq(at + ": формула", Norm(printed) ?? (decimal.TryParse(formula, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _) ? formula : null), formula);
    }

    private static string? Norm(string? f) =>
        string.IsNullOrWhiteSpace(f) ? null : f.Replace(" ", "").Replace(',', '.').TrimStart('=');

    private static string Set(IEnumerable<string?> items) =>
        string.Join(" | ", items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).Distinct().Order());

    private sealed class Diff
    {
        private readonly List<string> _lines = [];

        public void Add(string line) => _lines.Add(line);

        /// <summary>Не разобранное в образце — расхождение само по себе: в эталоне разобрано всё.</summary>
        public void Unparsed(IEnumerable<Unparsed> unparsed) =>
            _lines.AddRange(unparsed.Select(u => $"не разобрано: {u.Place}, строка {u.Line}: {u.Reason}"));

        public void Eq<T>(string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                _lines.Add($"{what}: эталон «{expected}», разбор «{actual}»");
        }

        public void Assert(string sample) =>
            Xunit.Assert.True(_lines.Count == 0, $"{sample}: расхождений {_lines.Count}\n" + string.Join("\n", _lines));
    }
}
