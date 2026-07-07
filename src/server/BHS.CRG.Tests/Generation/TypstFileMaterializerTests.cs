using System.Text.Json.Nodes;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Tests.Integration;
using PdfSharpCore.Pdf;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Материализация полей-вложений ({$type:"file"}) для Typst: скачивание blob, подстановка src+pageCount,
/// обход вложенных структур, graceful-skip при отсутствии blob.
/// </summary>
public class TypstFileMaterializerTests
{
    private static (List<(byte[] bytes, string ext)> placed, Func<byte[], string, string> place) Recorder()
    {
        var placed = new List<(byte[], string)>();
        Func<byte[], string, string> place = (b, ext) => { placed.Add((b, ext)); return $"assets/att_{placed.Count - 1}.{ext}"; };
        return (placed, place);
    }

    private static JsonObject FileNode(string blobPath, string fileName, string mimeType) => new()
    {
        ["$type"] = "file", ["blobPath"] = blobPath, ["fileName"] = fileName, ["mimeType"] = mimeType, ["size"] = 3,
    };

    [Fact]
    public async Task Materialize_ScalarFileField_ReplacedWithSrc()
    {
        var blob = new FakeBlobStorage();
        var path = await blob.UploadAsync("Схема.png", new MemoryStream([1, 2, 3]), "image/png");
        var root = new JsonObject { ["Файл"] = FileNode(path, "Схема.png", "image/png") };
        var (placed, place) = Recorder();

        await new TypstFileMaterializer(blob).MaterializeAsync(root, place);

        var f = root["Файл"]!.AsObject();
        Assert.Equal("assets/att_0.png", f["src"]!.GetValue<string>());
        Assert.Equal("Схема.png", f["fileName"]!.GetValue<string>());
        Assert.Equal("image/png", f["mimeType"]!.GetValue<string>());
        Assert.False(f.ContainsKey("$type"));   // внутренние поля вычищены
        Assert.False(f.ContainsKey("blobPath"));
        Assert.Single(placed);
        Assert.Equal([1, 2, 3], placed[0].bytes);
        Assert.Equal("png", placed[0].ext);
    }

    [Fact]
    public async Task Materialize_FileInsideArray_ElementReplaced()
    {
        var blob = new FakeBlobStorage();
        var path = await blob.UploadAsync("scan.jpg", new MemoryStream([9]), "image/jpeg");
        var root = new JsonObject { ["Приложения"] = new JsonArray(FileNode(path, "scan.jpg", "image/jpeg")) };
        var (_, place) = Recorder();

        await new TypstFileMaterializer(blob).MaterializeAsync(root, place);

        var el = root["Приложения"]!.AsArray()[0]!.AsObject();
        Assert.Equal("assets/att_0.jpg", el["src"]!.GetValue<string>());
    }

    [Fact]
    public async Task Materialize_MissingBlob_LeavesNodeUntouched()
    {
        var blob = new FakeBlobStorage();
        var root = new JsonObject { ["Файл"] = FileNode("нет-такого/blob", "x.pdf", "application/pdf") };
        var (placed, place) = Recorder();

        await new TypstFileMaterializer(blob).MaterializeAsync(root, place);

        var f = root["Файл"]!.AsObject();
        Assert.Equal("file", f["$type"]!.GetValue<string>());   // не тронут
        Assert.False(f.ContainsKey("src"));
        Assert.Empty(placed);                                    // приёмник не вызван
    }

    [Fact]
    public async Task Materialize_Pdf_AddsPageCount()
    {
        var blob = new FakeBlobStorage();
        var path = await blob.UploadAsync("doc.pdf", new MemoryStream(MakePdf(3)), "application/pdf");
        var root = new JsonObject { ["Файл"] = FileNode(path, "doc.pdf", "application/pdf") };
        var (_, place) = Recorder();

        await new TypstFileMaterializer(blob).MaterializeAsync(root, place);

        var f = root["Файл"]!.AsObject();
        Assert.Equal(3, f["pageCount"]!.GetValue<int>());
        Assert.Equal("assets/att_0.pdf", f["src"]!.GetValue<string>());
    }

    [Fact]
    public async Task Materialize_NonFileObjects_Untouched()
    {
        var blob = new FakeBlobStorage();
        var root = new JsonObject { ["Реквизит"] = "значение", ["Вложенный"] = new JsonObject { ["a"] = 1 } };
        var (placed, place) = Recorder();

        await new TypstFileMaterializer(blob).MaterializeAsync(root, place);

        Assert.Equal("значение", root["Реквизит"]!.GetValue<string>());
        Assert.Empty(placed);
    }

    private static byte[] MakePdf(int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}
