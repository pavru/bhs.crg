using System.Text;
using BHS.CRG.Modules.Files;

namespace BHS.CRG.Tests;

/// <summary>Вид файла по содержимому (issue #1265): подписи, расширение как уточнение, закрытый перечень.</summary>
public class FileKindsTests
{
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00];

    [Fact]
    public void Подписи_изображений_и_PDF_узнаются_без_имени()
    {
        Assert.Equal(FileKinds.Png, FileKinds.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], null));
        Assert.Equal(FileKinds.Jpeg, FileKinds.Detect([0xFF, 0xD8, 0xFF, 0xE0], null));
        Assert.Equal(FileKinds.Gif, FileKinds.Detect("GIF89a..."u8, null));
        Assert.Equal(FileKinds.WebP, FileKinds.Detect("RIFF\0\0\0\0WEBPVP8 "u8, null));
        Assert.Equal(FileKinds.Pdf, FileKinds.Detect("%PDF-1.7"u8, null));
    }

    /// <summary>Сканеры пишут перед подписью PDF свой мусор — читающие программы ищут её в первом килобайте.</summary>
    [Fact]
    public void Подпись_PDF_ищется_в_первом_килобайте_и_не_дальше()
    {
        var near = Encoding.ASCII.GetBytes(new string(' ', 300) + "%PDF-1.4");
        var far = Encoding.ASCII.GetBytes(new string(' ', FileKinds.HeadBytes) + "%PDF-1.4");

        Assert.Equal(FileKinds.Pdf, FileKinds.Detect(near, "скан.pdf"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect(far, "скан.pdf"));
    }

    [Theory]
    [InlineData("счёт.xlsx", FileKinds.Xlsx)]
    [InlineData("счёт.XLSX", FileKinds.Xlsx)]
    [InlineData("счёт.docx", FileKinds.Docx)]
    [InlineData("счёт.pdf", FileKinds.Unknown)]
    [InlineData("счёт", FileKinds.Unknown)]
    [InlineData(null, FileKinds.Unknown)]
    public void Архив_становится_офисным_файлом_только_с_расширением(string? name, string expected)
        => Assert.Equal(expected, FileKinds.Detect(Zip, name));

    [Theory]
    [InlineData("счёт.xls", FileKinds.Xls)]
    [InlineData("счёт.doc", FileKinds.Unknown)]
    public void Контейнер_OLE_читается_только_как_старый_Excel(string name, string expected)
        => Assert.Equal(expected, FileKinds.Detect(Ole, name));

    /// <summary>Содержимое главнее имени: расширение уточняет, но не назначает.</summary>
    [Fact]
    public void Расширение_не_назначает_вид()
    {
        Assert.Equal(FileKinds.Pdf, FileKinds.Detect("%PDF-1.7"u8, "счёт.xlsx"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect("просто текст"u8, "счёт.pdf"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect("<svg xmlns='http://www.w3.org/2000/svg'/>"u8, "знак.png"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect([], "пусто.pdf"));
    }

    /// <summary>Архив, в котором пять байт подписи PDF встретились случайно, PDF не становится.</summary>
    [Fact]
    public void Архив_с_подписью_PDF_внутри_остаётся_архивом()
    {
        byte[] zipWithPdf = [.. Zip, .. "%PDF-1.7"u8];

        Assert.Equal(FileKinds.Unknown, FileKinds.Detect(zipWithPdf, "вложение.zip"));
        Assert.Equal(FileKinds.Xlsx, FileKinds.Detect(zipWithPdf, "счёт.xlsx"));
    }

    [Fact]
    public async Task По_потоку_ответ_тот_же_и_короткий_файл_не_отказ()
    {
        using var pdf = new MemoryStream("%PDF-1.7 и дальше"u8.ToArray());
        using var tiny = new MemoryStream([0xFF]);

        Assert.Equal(FileKinds.Pdf, await FileKinds.DetectAsync(pdf, null));
        Assert.Equal(FileKinds.Unknown, await FileKinds.DetectAsync(tiny, "x.jpg"));
    }

    [Theory]
    [InlineData("application/pdf", FileKinds.Pdf)]
    [InlineData("Image/JPEG", FileKinds.Jpeg)]
    [InlineData("text/html", FileKinds.Unknown)]
    [InlineData("image/svg+xml", FileKinds.Unknown)]
    [InlineData("", FileKinds.Unknown)]
    [InlineData(null, FileKinds.Unknown)]
    public void Записанный_вид_отдаётся_только_из_перечня(string? stored, string expected)
        => Assert.Equal(expected, FileKinds.Served(stored));
}
