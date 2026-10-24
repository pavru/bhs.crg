using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Файл ГРАНД-Сметы — недоверенный вход (plan-05, ревью PR 1/2): то, что проходит схему или приходит не из
/// программы, разбирается или отвергается названной причиной — без чужого исключения наружу, без переполнения
/// стека и без файла целиком в памяти.
/// </summary>
public class GrandSmetaUntrustedInputTests
{
    private const string Lsr = "lsr-16.2.2-eom";
    private const string Ka = "ka-16.2.0-vn-summary";
    private const string OutOfRange = "число вне допустимого диапазона";

    /// <summary>Схема допускает <c>Quantity/@Precision</c> от 7 до «-3» — округление до тысяч.</summary>
    [Fact]
    public void Точность_минус_три_округляет_количество_материала_до_тысяч()
    {
        var original = LocalEstimateReader.Read(GrandSmetaSamples.Open(Lsr));
        var target = original.Positions.Where(p => p.Materials.Count > 0).MaxBy(p => p.Materials.Max(m => m.Quantity))!;

        var m = LocalEstimateReader.Read(GrandSmetaSamples.OpenEditedXml(Lsr,
            x => PositionOf(x, target.Number).Element("Quantity")!.SetAttributeValue("Precision", "-3")));

        Assert.Empty(m.Unparsed);
        Assert.Equal(
            target.Materials.Select(x => Math.Round(x.Quantity / 1000, MidpointRounding.AwayFromZero) * 1000),
            m.Positions.Single(p => p.Number == target.Number).Materials.Select(x => x.Quantity));
    }

    [Fact]
    public void Счёт_позиции_за_пределами_decimal_уводит_её_в_не_разобранные()
    {
        var number = LocalEstimateReader.Read(GrandSmetaSamples.Open(Lsr)).Positions.First(p => p.Materials.Count > 0).Number;

        var m = LocalEstimateReader.Read(GrandSmetaSamples.OpenEditedXml(Lsr, x =>
        {
            var p = PositionOf(x, number);
            p.Element("Quantity")!.SetAttributeValue("Result", "100000000000000000000");
            p.Elements("Resources").Elements("Mat").First().SetAttributeValue("Quantity", "100000000000000000000");
        }));

        var bad = Assert.Single(m.Unparsed);
        Assert.Contains($"позиция {number} ", bad.Place + " ");
        Assert.Equal(OutOfRange, bad.Reason);
    }

    [Fact]
    public void Счёт_вне_позиций_за_пределами_decimal_отвергает_документ()
    {
        var file = GrandSmetaSamples.OpenEditedXml(Lsr, x =>
        {
            var totals = x.Descendants("Chapter").Elements("Itog").Descendants("Itog")
                .Where(i => (string?)i.Attribute("DataType") == "ChapterTotal").ToList();
            Assert.True(totals.Count >= 2);
            foreach (var t in totals) t.SetAttributeValue("PZ", "50000000000000000000000000000");
        });

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.DocumentInvalid, ex.Reason);
        Assert.Contains(OutOfRange, ex.Message);
    }

    /// <summary>Формула цены разбирается рекурсивным спуском: без предела вложенности длинная строка скобок
    /// переполнила бы стек — а это падение процесса, не исключение.</summary>
    [Theory]
    [InlineData("(", ")")]
    [InlineData("-", "")]
    public void Слишком_глубокая_формула_цены_не_число_а_не_падение(string open, string close)
    {
        const int depth = 100_000;
        var formula = string.Concat(Enumerable.Repeat(open, depth)) + "1" + string.Concat(Enumerable.Repeat(close, depth));
        var file = GrandSmetaSamples.OpenEdited(Ka, x => x.Replace("OptPrice=\"5326,23\"", $"OptPrice=\"{formula}\""));

        var bad = Assert.Single(ConjunctureReader.Read(file).Unparsed);

        Assert.StartsWith("OptPrice — не число", bad.Reason);
    }

    [Fact]
    public void Скобки_в_формуле_цены_в_пределе_вложенности_вычисляются()
    {
        var file = GrandSmetaSamples.OpenEdited(Ka, x => x.Replace("OptPrice=\"5326,23\"", "OptPrice=\"((5326,23))*(1)\""));

        var m = ConjunctureReader.Read(file);

        Assert.Empty(m.Unparsed);
        Assert.Contains(m.Positions.SelectMany(p => p.Offers), o => o.SellingPrice == 5326.23m);
    }

    [Fact]
    public void Неразрывный_пробел_между_разрядами_цены_не_мешает_числу()
    {
        var file = GrandSmetaSamples.OpenEdited(Ka, x => x.Replace("OptPrice=\"5326,23\"", "OptPrice=\"5 326,23\""));

        var m = ConjunctureReader.Read(file);

        Assert.Empty(m.Unparsed);
        Assert.Contains(m.Positions.SelectMany(p => p.Offers), o => o.SellingPrice == 5326.23m);
    }

    /// <summary>Без пролога программа пишет windows-1251; тот же текст в UTF-8 (пересохранён сторонним
    /// инструментом) читается как UTF-8, а не превращается в «Р­Р»РµРєС‚…» при «разобрано всё».</summary>
    [Theory]
    [InlineData("lsr-16.2.0-vn")]
    [InlineData("vor-16.2.3-variables-hyperlinks")]
    public void Data_xml_без_пролога_читается_и_в_windows_1251_и_в_UTF_8(string sample)
    {
        var expected = Json(GrandSmetaSamples.Open(sample));
        var text = GrandSmetaSamples.DataXmlText(sample);
        var bare = Regex.Replace(text, @"^<\?xml[^>]*\?>\s*", "");
        Assert.NotEqual(text, bare);

        Assert.Equal(expected, Json(GrandSmetaFile.Open(new MemoryStream(GrandSmetaSamples.Cp1251.GetBytes(bare)))));
        Assert.Equal(expected, Json(GrandSmetaFile.Open(new MemoryStream(new UTF8Encoding(false).GetBytes(bare)))));
    }

    /// <summary>Потолок голого Data.xml — тот же, что у распакованного: поток без конца отвергнут, а не дочитан.</summary>
    [Fact]
    public void Голый_XML_больше_потолка_отвергнут_не_дочитанным()
    {
        var ex = Assert.Throws<GrandSmetaRefusedException>(() => GrandSmetaFile.Open(new EndlessStream()));

        Assert.Equal(GrandSmetaRefusal.NotGrandSmeta, ex.Reason);
        Assert.Contains("больше 64 МБ", ex.Message);
    }

    [Fact]
    public void Data_xml_в_архиве_больше_потолка_отвергнут()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var data = zip.CreateEntry("Data.xml", CompressionLevel.Fastest).Open();
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < 65; i++) data.Write(chunk);
        }

        archive.Position = 0;
        var ex = Assert.Throws<GrandSmetaRefusedException>(() => GrandSmetaFile.Open(archive));

        Assert.Equal(GrandSmetaRefusal.NotGrandSmeta, ex.Reason);
        Assert.Contains("больше 64 МБ", ex.Message);
    }

    /// <summary>Поток без перемотки (тело запроса) читается так же, как файл: архив буферизуется сам.</summary>
    [Fact]
    public void Gsfx_из_потока_без_перемотки_даёт_ту_же_модель()
    {
        var expected = Json(GrandSmetaSamples.Open(Lsr));

        var actual = Json(GrandSmetaFile.Open(new ForwardOnlyStream(new MemoryStream(GrandSmetaSamples.Gsfx(Lsr)))));

        Assert.Equal(expected, actual);
    }

    private static XElement PositionOf(XDocument x, string number) =>
        x.Root!.Elements("Chapters").Elements("Chapter").Elements("Position")
            .First(p => (string?)p.Attribute("Number") == number);

    private static string Json(GrandSmetaFile file)
    {
        object model = file.Kind switch
        {
            GrandSmetaKind.LocalEstimate => LocalEstimateReader.Read(file),
            GrandSmetaKind.VolumeStatement => VolumeStatementReader.Read(file),
            _ => ConjunctureReader.Read(file),
        };
        return JsonSerializer.Serialize(model, model.GetType());
    }

    /// <summary>Перематываемый поток пробелов без конца.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => throw new NotSupportedException(),
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
