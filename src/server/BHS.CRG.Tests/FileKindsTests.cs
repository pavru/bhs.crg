using System.IO.Compression;
using System.Text;
using BHS.CRG.Modules.Files;

namespace BHS.CRG.Tests;

/// <summary>Вид файла по содержимому (issue #1265): подписи, оглавление офисного файла, закрытый перечень.</summary>
public class FileKindsTests
{
    [Fact]
    public void Подписи_изображений_и_PDF_узнаются()
    {
        Assert.Equal(FileKinds.Png, FileKinds.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal(FileKinds.Jpeg, FileKinds.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(FileKinds.Gif, FileKinds.Detect("GIF89a..."u8));
        Assert.Equal(FileKinds.WebP, FileKinds.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(FileKinds.Bmp, FileKinds.Detect([(byte)'B', (byte)'M', 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0]));
        Assert.Equal(FileKinds.Pdf, FileKinds.Detect("%PDF-1.7\n"u8));
    }

    /// <summary>
    /// Подпись PDF — в начале и с версией. «Где-то в первом килобайте» делало PDF из любой страницы,
    /// в тексте которой встретились эти пять букв (ревью PR #1275).
    /// </summary>
    [Theory]
    [InlineData("%PDF-1.4 и дальше", true)]
    [InlineData("﻿%PDF-2.0", true)]
    [InlineData("\r\n  %PDF-1.7", true)]
    [InlineData("<html>%PDF-1.4</html>", false)]
    [InlineData("заметка про %PDF-1.4", false)]
    [InlineData("%PDF-не настоящий", false)]
    [InlineData("%PDF-", false)]
    [InlineData("BM — так начинается и обычный текст", false)]
    [InlineData("", false)]
    public void PDF_узнаётся_по_подписи_с_версией_в_самом_начале(string text, bool pdf)
        => Assert.Equal(pdf ? FileKinds.Pdf : FileKinds.Unknown, FileKinds.Detect(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Офисный файл узнаётся по оглавлению архива, а не по расширению: имя в определении не
    /// участвует вовсе, так что переименованный архив таблицей не становится.
    /// </summary>
    [Theory]
    [InlineData("xl/workbook.xml", FileKinds.Xlsx)]
    [InlineData("word/document.xml", FileKinds.Docx)]
    [InlineData("readme.txt", FileKinds.Unknown)]
    [InlineData("xl/workbook.bin", FileKinds.Unknown)]
    public async Task Архив_офисный_только_с_главной_частью_в_оглавлении(string entry, string expected)
    {
        using var zip = new MemoryStream(Zip("[Content_Types].xml", entry));

        Assert.Equal(expected, await FileKinds.DetectAsync(zip));
    }

    [Fact]
    public async Task Обрезанный_архив_не_отказ_а_неизвестный_файл()
    {
        using var cut = new MemoryStream(Zip("xl/workbook.xml")[..40]);

        Assert.Equal(FileKinds.Unknown, await FileKinds.DetectAsync(cut));
    }

    /// <summary>
    /// Старый Excel — контейнер с потоком «Workbook». Документ Word со вставленной таблицей несёт и
    /// его, и «WordDocument» — таблицей он не считается. Имя потока на границе кусков чтения не
    /// теряется.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Workbook" }, 0, FileKinds.Xls)]
    // Книга старше Excel 97 зовёт свой поток «Book» (issue #1268) — так сохраняют счета учётные
    // программы. Имя короткое, поэтому считается только записью каталога: на границе в 128 байт.
    [InlineData(new[] { "Book" }, 120, FileKinds.Xls)]
    [InlineData(new[] { "Book" }, 81920 - 8, FileKinds.Xls)]
    [InlineData(new[] { "Book" }, 121, FileKinds.Unknown)]
    [InlineData(new[] { "Bookmarks" }, 120, FileKinds.Unknown)]
    [InlineData(new[] { "Book", "WordDocument" }, 120, FileKinds.Unknown)]
    [InlineData(new[] { "Workbook" }, 81920 - 9, FileKinds.Xls)]
    [InlineData(new[] { "WordDocument" }, 0, FileKinds.Unknown)]
    [InlineData(new[] { "Workbook", "WordDocument" }, 200_000, FileKinds.Unknown)]
    [InlineData(new string[0], 1000, FileKinds.Unknown)]
    public async Task Контейнер_OLE_старый_Excel_только_с_потоком_книги(string[] streams, int padding, string expected)
    {
        using var ole = new MemoryStream(Ole(padding, streams));

        Assert.Equal(expected, await FileKinds.DetectAsync(ole));
    }

    /// <summary>По одному началу офисный файл не узнать — и догадки здесь нет.</summary>
    [Fact]
    public void По_началу_офисный_файл_не_определяется()
    {
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect(Zip("xl/workbook.xml").AsSpan(0, FileKinds.HeadBytes)));
        Assert.Equal(FileKinds.Unknown, FileKinds.Detect(Ole(0, "Workbook").AsSpan(0, FileKinds.HeadBytes)));
    }

    /// <summary>
    /// Отдача: что узнаётся по подписи — узнаётся заново, запись не читается. Офисному файлу
    /// верим записи, только если начало файла ей не противоречит.
    /// </summary>
    [Fact]
    public void При_отдаче_запись_вида_не_перекрывает_содержимое()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var zip = Zip("xl/workbook.xml");
        var html = "<html></html>"u8;

        Assert.Equal(FileKinds.Png, FileKinds.Served(png, "application/pdf"));
        Assert.Equal(FileKinds.Png, FileKinds.Served(png, "text/html"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Served(html, "application/pdf"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Served(html, FileKinds.Xlsx));
        Assert.Equal(FileKinds.Xlsx, FileKinds.Served(zip, FileKinds.Xlsx));
        Assert.Equal(FileKinds.Docx, FileKinds.Served(zip, FileKinds.Docx));
        Assert.Equal(FileKinds.Unknown, FileKinds.Served(zip, "application/zip"));
        Assert.Equal(FileKinds.Unknown, FileKinds.Served(zip, FileKinds.Xls));
        Assert.Equal(FileKinds.Xls, FileKinds.Served(Ole(0, "Workbook"), FileKinds.Xls));
        Assert.Equal(FileKinds.Unknown, FileKinds.Served(Ole(0, "Workbook"), null));
    }

    [Theory]
    [InlineData("application/pdf", FileKinds.Pdf)]
    [InlineData("application/x-pdf", FileKinds.Pdf)]
    [InlineData("Image/JPEG", FileKinds.Jpeg)]
    [InlineData("image/jpg", FileKinds.Jpeg)]
    [InlineData("image/pjpeg", FileKinds.Jpeg)]
    [InlineData("image/x-png", FileKinds.Png)]
    [InlineData("text/html", FileKinds.Unknown)]
    [InlineData("image/svg+xml", FileKinds.Unknown)]
    [InlineData("", FileKinds.Unknown)]
    [InlineData(null, FileKinds.Unknown)]
    public void Записанный_вид_приводится_к_известному(string? stored, string expected)
        => Assert.Equal(expected, FileKinds.Recorded(stored));

    internal static byte[] Zip(params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                    writer.Write("<x/>");
        return buffer.ToArray();
    }

    /// <summary>Подпись контейнера, заполнитель и имена потоков в UTF-16 — как в его каталоге.</summary>
    internal static byte[] Ole(int padding, params string[] streams) =>
    [
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1,
        .. new byte[padding],
        .. streams.SelectMany(name => Encoding.Unicode.GetBytes(name).Concat(new byte[64])),
        .. new byte[128],
    ];
}
