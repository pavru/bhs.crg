using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Образцы файлов заказчика (<c>TestData/GrandSmeta</c>, опись — README там же) и их эталоны: контрольные точки,
/// снятые с печатных форм программы (<c>etalons_from_forms.py</c>, plan-05 вариант Г). Файлы читаются из
/// репозитория, в выходную папку не копируются.
/// </summary>
internal static class GrandSmetaSamples
{
    public static readonly string[] Lsr = ["lsr-16.2.0-vn", "lsr-16.2.2-eom", "lsr-16.2.2-os-skud", "lsr-16.2.2-sks"];

    public static readonly string[] Vor =
    [
        "vor-15.1-attachments-2pdf", "vor-15.3.5-attachment-pdf", "vor-15.3.7-no-attachments",
        "vor-16.2.3-variables-hyperlinks",
    ];

    public static readonly string[] Ka =
    [
        "ka-16.2.0-eom-materials", "ka-16.2.0-eom-summary", "ka-16.2.0-os-skud-summary", "ka-16.2.0-sks-equipment",
        "ka-16.2.0-sks-summary", "ka-16.2.0-vn-summary",
    ];

    /// <summary>ЛСР РИМ с итогами — из пар «с итогом / без итогов» (plan-05, 0.3). Печатных форм с итогом к ним
    /// нет, эталона тоже: проверяются инварианты и досчёт пары.</summary>
    public static readonly string[] LsrRim = ["lsr-16.2.3-rim1", "lsr-16.2.3-rim2"];

    /// <summary>Пары владельца 24.10.2026: та же смета, пересохранённая программой 16.3.0 без итогов, — и с итогом.</summary>
    public static readonly (string NoTotals, string WithTotals)[] Pairs =
    [
        ("lsr-16.3.0-vn-no-itog", "lsr-16.2.0-vn"), ("lsr-16.3.0-eom-no-itog", "lsr-16.2.2-eom"),
        ("lsr-16.3.0-os-skud-no-itog", "lsr-16.2.2-os-skud"), ("lsr-16.3.0-sks-no-itog", "lsr-16.2.2-sks"),
        ("lsr-16.3.0-rim1-no-itog", "lsr-16.2.3-rim1"), ("lsr-16.3.0-rim2-no-itog", "lsr-16.2.3-rim2"),
    ];

    public static IEnumerable<string> All => Lsr.Concat(LsrRim).Concat(Pairs.Select(p => p.NoTotals)).Concat(Vor).Concat(Ka);

    static GrandSmetaSamples() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Cp1251 => Encoding.GetEncoding(1251);

    public static string Path(params string[] parts) =>
        RepoPaths.File(["src", "server", "BHS.CRG.Tests", "TestData", "GrandSmeta", .. parts]);

    public static byte[] Gsfx(string sample) => File.ReadAllBytes(Path(sample + ".gsfx"));

    public static GrandSmetaFile Open(string sample) => GrandSmetaFile.Open(new MemoryStream(Gsfx(sample)));

    /// <summary>Голый <c>Data.xml</c> образца — побайтно, как лежит в архиве.</summary>
    public static byte[] DataXml(string sample)
    {
        using var zip = new ZipArchive(new MemoryStream(Gsfx(sample)), ZipArchiveMode.Read);
        using var data = zip.GetEntry("Data.xml")!.Open();
        using var copy = new MemoryStream();
        data.CopyTo(copy);
        return copy.ToArray();
    }

    public static string DataXmlText(string sample) => Cp1251.GetString(DataXml(sample));

    /// <summary>Образец с правкой текста <c>Data.xml</c> — испорченная позиция, чужая версия, удалённые итоги.</summary>
    public static GrandSmetaFile OpenEdited(string sample, Func<string, string> edit)
    {
        var text = DataXmlText(sample);
        var edited = edit(text);
        Assert.NotEqual(text, edited);
        return GrandSmetaFile.Open(new MemoryStream(Cp1251.GetBytes(edited)));
    }

    /// <summary>То же правкой дерева — когда текстом не вырезать (итоги вложены друг в друга).</summary>
    public static GrandSmetaFile OpenEditedXml(string sample, Action<XDocument> edit)
    {
        var doc = XDocument.Load(new MemoryStream(DataXml(sample)));
        edit(doc);
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = Cp1251 }))
            doc.Save(writer);
        return GrandSmetaFile.Open(new MemoryStream(buffer.ToArray()));
    }

    public static JsonElement Etalon(string sample)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path(sample + ".etalon.json")));
        return doc.RootElement.Clone();
    }
}

/// <summary>Чтение эталона: числа в нём — строками, ровно как в ячейке формы.</summary>
internal static class EtalonJson
{
    public static string? Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static decimal? Dec(this JsonElement e, string name) =>
        e.Str(name) is { } s ? decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;

    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static bool Has(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
}
