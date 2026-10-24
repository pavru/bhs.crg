using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Разбор файлов ГРАНД-Сметы (change <c>plan-05-grand-smeta-reader</c>, ТЗ PLAN-7, PLAN-8, STG-12): вид и версия
/// до разбора, отказ на чужой версии, не разобранное — поимённо, файл без итогов — отказ. Совпадение с
/// эталонами печатных форм — <see cref="GrandSmetaEtalonTests" />.
/// </summary>
public class GrandSmetaReaderTests
{
    public static TheoryData<string> AllSamples => new(GrandSmetaSamples.All);
    public static TheoryData<string> LsrSamples => new(GrandSmetaSamples.Lsr);

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Упакованный_gsfx_и_голый_Data_xml_дают_одну_модель(string sample)
    {
        var zipped = GrandSmetaSamples.Open(sample);
        var bare = GrandSmetaFile.Open(new MemoryStream(GrandSmetaSamples.DataXml(sample)));

        Assert.Equal(zipped.Kind, bare.Kind);
        Assert.Equal(Json(Read(zipped)), Json(Read(bare)));
    }

    /// <summary>Инварианты на всех ЛСР: разобрано всё; итог раздела, записанный программой, — сумма «Всего по
    /// позиции» активных позиций; итог документа — сумма разделов.</summary>
    [Theory]
    [MemberData(nameof(LsrSamples))]
    public void ЛСР_разобрана_вся_итог_раздела_равен_сумме_позиций(string sample)
    {
        var m = LocalEstimateReader.Read(GrandSmetaSamples.Open(sample));

        Assert.Empty(m.Unparsed);
        Assert.Equal(GrandSmetaSamples.DataXmlText(sample).Split("<Position ").Length - 1, m.PositionsInFile);
        foreach (var chapter in m.Chapters)
            Assert.Equal(chapter.Total, chapter.Positions.Where(p => !p.IsInactive).Sum(p => p.Total));
        Assert.Equal(m.Totals.Total, m.Chapters.Sum(c => c.Total));
        Assert.All(m.Positions, p => Assert.Equal(TotalSource.Program, p.TotalSource));
    }

    [Theory]
    [MemberData(nameof(LsrSamples))]
    public void Четыре_ЛСР_опознаны_базисно_индексными(string sample) =>
        Assert.Equal(CalculationMethod.Bim, LocalEstimateReader.Read(GrandSmetaSamples.Open(sample)).Method);

    /// <summary>Пары РИМ ещё не в репозитории (plan-05 0.3): признаки метода — на фрагменте, повторяющем их
    /// <c>Indexes</c> (<c>IndexesMode="None"</c> и индексы ГОСР).</summary>
    [Theory]
    [InlineData("<Indexes IndexesMode=\"PosOne\" IndexesLinkMode=\"Category\" />", CalculationMethod.Bim)]
    [InlineData("<Indexes IndexesMode=\"None\"><IndexesRes><Index Code=\"561\" MT=\"1,2\" /></IndexesRes></Indexes>", CalculationMethod.Rim)]
    [InlineData("<Indexes IndexesMode=\"None\" />", CalculationMethod.Rm)]
    [InlineData("<Parameters />", CalculationMethod.Unknown)]
    public void Метод_расчёта_по_Indexes(string fragment, CalculationMethod expected) =>
        Assert.Equal(expected, LocalEstimateReader.DetectMethod(XElement.Parse($"<Document>{fragment}</Document>")));

    [Fact]
    public void ЛСР_без_итогов_отвергнута_с_текстом()
    {
        var file = GrandSmetaSamples.OpenEditedXml("lsr-16.2.0-vn",
            x => x.Descendants("Position").Elements("Itog").Remove());

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.NoTotals, ex.Reason);
        Assert.Contains("в файле нет итогов", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Неизвестная_версия_программы_отвергнута_с_версией_и_перечнем()
    {
        var ex = Assert.Throws<GrandSmetaRefusedException>(() => GrandSmetaSamples.OpenEdited("lsr-16.2.0-vn",
            x => x.Replace("ProgramVersion=\"16.2.0.58891\"", "ProgramVersion=\"17.0.0.1\"")));

        Assert.Equal(GrandSmetaRefusal.UnsupportedVersion, ex.Reason);
        Assert.Contains("версии 17.0.0.1 ", ex.Message);
        Assert.Contains("не поддерживается; поддерживаются: 16.2, 16.3", ex.Message);
    }

    [Fact]
    public void Неизвестный_вид_документа_отвергнут()
    {
        var ex = Assert.Throws<GrandSmetaRefusedException>(() => GrandSmetaSamples.OpenEdited("lsr-16.2.0-vn",
            x => x.Replace("EEBE36B7D340", "EEBE36B7D399")));

        Assert.Equal(GrandSmetaRefusal.UnknownKind, ex.Reason);
    }

    [Fact]
    public void Не_ГРАНД_Смета_отвергнута()
    {
        var ex = Assert.Throws<GrandSmetaRefusedException>(() =>
            GrandSmetaFile.Open(new MemoryStream("<html><body/></html>"u8.ToArray())));

        Assert.Equal(GrandSmetaRefusal.NotGrandSmeta, ex.Reason);
    }

    [Fact]
    public void Файл_другого_вида_читателю_не_подходит()
    {
        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(GrandSmetaSamples.Open("ka-16.2.0-vn-summary")));

        Assert.Equal(GrandSmetaRefusal.WrongKind, ex.Reason);
    }

    /// <summary>Испорченная позиция — «N−1 из N»: она в не разобранных с номером строки и текстом ошибки схемы,
    /// остальные разобраны (PLAN-7). Позиция — шестая в файле, «Коммутатор доступа».</summary>
    [Fact]
    public void Позиция_не_прошедшая_схему_уходит_в_не_разобранные_с_номером_строки()
    {
        var file = GrandSmetaSamples.OpenEdited("lsr-16.2.0-vn", x => Nth(x, "<Position ", 6, "<Position Bogus=\"1\" "));

        var m = LocalEstimateReader.Read(file);

        var bad = Assert.Single(m.Unparsed);
        Assert.Equal(25, m.PositionsInFile);
        Assert.Equal(24, m.Positions.Count());
        Assert.Contains("позиция 6 ", bad.Place);
        Assert.Contains("Bogus", bad.Reason);
        Assert.Equal(LineOf(GrandSmetaSamples.DataXmlText("lsr-16.2.0-vn"), "<Position ", 6), bad.Line);
    }

    [Fact]
    public void Позиция_с_нечитаемым_значением_уходит_в_не_разобранные()
    {
        var file = GrandSmetaSamples.OpenEdited("ka-16.2.0-vn-summary",
            x => x.Replace("OptPrice=\"5326,23\"", "OptPrice=\"пять тысяч\""));

        var m = ConjunctureReader.Read(file);

        var bad = Assert.Single(m.Unparsed);
        Assert.Contains("OptPrice — не число", bad.Reason);
        Assert.Equal(14, m.PositionsInFile);
    }

    [Fact]
    public void Документ_без_разделов_отвергнут_а_не_пуст()
    {
        var file = GrandSmetaSamples.OpenEdited("vor-15.3.7-no-attachments",
            x => Regex.Replace(x, "<Chapters>.*</Chapters>", "<Chapters />", RegexOptions.Singleline));

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => VolumeStatementReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.DocumentInvalid, ex.Reason);
    }

    /// <summary>Ошибка схемы вне позиций — отказ документа, а не «разобрано всё»: там реквизиты и настройки
    /// расчёта, на которых стоят все позиции.</summary>
    [Fact]
    public void Ошибка_схемы_вне_позиций_отвергает_документ()
    {
        var file = GrandSmetaSamples.OpenEdited("lsr-16.2.0-vn", x => x.Replace("<Properties ", "<Properties Bogus=\"1\" "));

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.DocumentInvalid, ex.Reason);
        Assert.Contains(GrandSmetaSchemas.Edition, ex.Message);
    }

    /// <summary>Нечитаемый реквизит — отказ документа своими словами, а не чужое исключение наружу.</summary>
    [Fact]
    public void Нечитаемое_значение_вне_позиций_отвергает_документ()
    {
        var file = GrandSmetaSamples.OpenEdited("ka-16.2.0-vn-summary",
            x => x.Replace("CurrPriceDate=\"25.03.2026\"", "CurrPriceDate=\"март\""));

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => ConjunctureReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.DocumentInvalid, ex.Reason);
        Assert.Contains("CurrPriceDate — не дата", ex.Message);
    }

    /// <summary>Строка по КА — пометка и ссылка на КА; её цена — сметная без НДС из ЛСР (PLAN-9).</summary>
    [Fact]
    public void Строка_ЛСР_по_КА_несёт_поставщика_цену_и_дату()
    {
        var m = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.0-vn"));

        var line = m.Positions.Single(p => p.Number == "2");
        Assert.True(line.IsSubordinate);
        Assert.NotNull(line.MarketAnalysis);
        Assert.Equal(5326.23m, line.MarketAnalysis.Price);
        Assert.Equal("7702680818", line.MarketAnalysis.Inn);
        Assert.Equal(new DateOnly(2026, 4, 13), line.MarketAnalysis.SellingDate);
        Assert.Equal(Math.Round(line.Total / line.Quantity, 2), line.UnitPrice);
    }

    [Fact]
    public void Неактивная_позиция_читается_с_пометкой()
    {
        var m = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.2-os-skud"));

        var inactive = Assert.Single(m.Positions, p => p.IsInactive);
        Assert.Equal("ГЭСНм08-02-427-01", inactive.Code);
        // Итог неактивной программа обнуляет, трудозатраты оставляет
        Assert.Equal(0m, inactive.Total);
        Assert.Equal(3.89088m, inactive.Labor);
    }

    /// <summary>Коэффициенты позиции — как в файле, без пересчёта (PLAN-8), с названием и значениями.</summary>
    [Fact]
    public void Коэффициенты_позиции_с_названием_и_значениями_итог_из_файла()
    {
        var m = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.0-vn"));

        var p = m.Positions.First(x => x.Number == "1");
        var k = Assert.Single(p.Coefficients);
        Assert.Equal("421/пр_2020_прил.10_т.5_п.1.1_гр.3", k.Code);
        Assert.EndsWith("ОЗП=1,2; ЭМ=1,2 к расх.; ЗПМ=1,2; ТЗ=1,2; ТЗМ=1,2", k.Caption);
        Assert.Equal(1667.79m, p.Total);
    }

    [Fact]
    public void ВОР_несёт_переменные_гиперссылки_и_вложения_без_содержимого()
    {
        var v = VolumeStatementReader.Read(GrandSmetaSamples.Open("vor-16.2.3-variables-hyperlinks"));
        Assert.Equal(22, v.Variables.Count);
        Assert.Equal(4, v.Hyperlinks.Count);
        Assert.Contains(v.Works, w => w.CalcRows.Any(r => r.IsTotal));

        var a = VolumeStatementReader.Read(GrandSmetaSamples.Open("vor-15.1-attachments-2pdf"));
        Assert.Equal(2, a.Attachments.Count);
        Assert.All(a.Attachments, x => Assert.EndsWith(".pdf", x.FileName));
    }

    private static object Read(GrandSmetaFile file) => file.Kind switch
    {
        GrandSmetaKind.LocalEstimate => LocalEstimateReader.Read(file),
        GrandSmetaKind.VolumeStatement => VolumeStatementReader.Read(file),
        _ => ConjunctureReader.Read(file),
    };

    private static string Json(object model) => JsonSerializer.Serialize(model, model.GetType());

    private static string Nth(string text, string what, int n, string with)
    {
        var at = -1;
        for (var i = 0; i < n; i++) at = text.IndexOf(what, at + 1, StringComparison.Ordinal);
        return text[..at] + with + text[(at + what.Length)..];
    }

    private static int LineOf(string text, string what, int n)
    {
        var at = -1;
        for (var i = 0; i < n; i++) at = text.IndexOf(what, at + 1, StringComparison.Ordinal);
        return text[..at].Count(c => c == '\n') + 1;
    }
}
