using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Своя редакция схемы (change <c>plan-05-grand-smeta-reader</c>, задача 1.2, ТЗ STG-12): на каждую правку BHS-N —
/// образец, который официальная схема 2026.2.3 отвергает, а своя пропускает. Официальные схемы лежат рядом с
/// образцами (<c>TestData/GrandSmeta/official-2026.2.3</c>) только ради этой сверки.
///
/// <para>Правка без образца — ослабление схемы наугад; образец, который официальная уже пропускает, — правка
/// лишняя. Оба случая этот тест ловит.</para>
/// </summary>
public class GrandSmetaSchemaTests
{
    public static TheoryData<string> AllSamples => new(GrandSmetaSamples.All);

    /// <summary>BHS-5 найдена на файле вне подборки (КА «(лоток)»): здесь — образец подборки с тем же расхождением,
    /// внесённым правкой текста. BHS-6 — на ЛСР РИМ, она в репозитории с парами «с итогом / без итогов».</summary>
    public static TheoryData<string, string, string> Patches => new()
    {
        { "BHS-1", "vor-16.2.3-variables-hyperlinks", "The 'GrandSmetaSign' attribute is not declared." },
        { "BHS-2", "vor-15.1-attachments-2pdf", "The 'Caption' attribute is not declared." },
        { "BHS-3", "lsr-16.2.2-eom", "The element 'PriceCurr' cannot contain child element 'Fx'" },
        { "BHS-3", "lsr-16.2.2-os-skud", "The element 'Item' has invalid child element 'PriceCurr'." },
        { "BHS-4", "lsr-16.2.2-eom", "The 'Comment' attribute is not declared." },
        { "BHS-5", "ka-16.2.0-vn-summary", "The required attribute 'SupplierKind' is missing." },
        { "BHS-6", "lsr-16.2.3-rim1", "invalid child element 'IdsGenerator'" },
    };

    [Theory]
    [MemberData(nameof(Patches))]
    public void Правка_схемы_пропускает_то_что_официальная_отвергает(string patch, string sample, string officialError)
    {
        var xml = Sample(patch, sample);
        var schema = GrandSmetaSamples.Open(sample).Schema;

        Assert.Contains(Errors(xml, Official(schema)), e => e.Contains(officialError, StringComparison.Ordinal));
        Assert.Empty(Errors(xml, GrandSmetaSchemas.Load(schema)));
        Assert.Contains(patch + ":", Shipped(schema.Resource));
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Все_образцы_проходят_свою_редакцию_без_ошибок(string sample)
    {
        var schema = GrandSmetaSamples.Open(sample).Schema;
        Assert.Empty(Errors(GrandSmetaSamples.DataXml(sample), GrandSmetaSchemas.Load(schema)));
    }

    [Fact]
    public void Своя_редакция_названа_в_самих_схемах()
    {
        foreach (var name in new[] { "OpenXML_LS.xsd", "OpenXML_KA.xsd", "OpenXML_VOW.xsd" })
            Assert.Contains($"version=\"{GrandSmetaSchemas.Edition}\"", Shipped(name));
    }

    /// <summary>Текст схемы, как она едет в сборке модуля, — ресурсом, а не файлом исходников.</summary>
    private static string Shipped(string resource)
    {
        using var stream = typeof(GrandSmetaSchemas).Assembly.GetManifestResourceStream("GrandSmeta.Schemas." + resource);
        Assert.NotNull(stream);
        return new StreamReader(stream).ReadToEnd();
    }

    private static byte[] Sample(string patch, string sample)
    {
        if (patch != "BHS-5") return GrandSmetaSamples.DataXml(sample);
        var text = GrandSmetaSamples.DataXmlText(sample);
        var edited = Regex.Replace(text, "(<Item [^>]*OptPrice[^>]*?) SupplierKind=\"Supplier\"", "$1");
        Assert.NotEqual(text, edited);
        return GrandSmetaSamples.Cp1251.GetBytes(edited);
    }

    private static XmlSchemaSet Official(GrandSmetaSchema schema)
    {
        var file = schema.Kind switch
        {
            GrandSmetaKind.LocalEstimate => "OpenXML_LS_2026.2.3.xsd",
            GrandSmetaKind.Conjuncture => "OpenXML_KA_2026.2.3.xsd",
            _ => "OpenXML-VOW 2026.2.3.xsd",
        };
        var set = new XmlSchemaSet();
        set.Add(null, GrandSmetaSamples.Path("official-2026.2.3", file));
        set.Compile();
        return set;
    }

    private static List<string> Errors(byte[] xml, XmlSchemaSet schemas)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Exception.LineNumber}: {e.Message}");
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read()) { }
        return errors;
    }
}
