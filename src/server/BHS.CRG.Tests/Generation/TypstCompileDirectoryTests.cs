using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Запись каталога компиляции — одна на генерацию и отладочный пакет (ТЗ CORE-21, CORE-22; change
/// core-generation-input-one-path, D3). Здесь — на подставном хранилище, без Typst: раскладка путей,
/// предел размера, отмена, шрифт, которого хранилище не отдало. Совпадение с пакетом на настоящем
/// Typst стережёт <see cref="GenerationDebugBundleParityTests" />.
/// </summary>
public sealed class TypstCompileDirectoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "compile-dir-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBlobStorage _store = new();
    private readonly GenerationBlobSpy _spy;

    public TypstCompileDirectoryTests()
    {
        Directory.CreateDirectory(_dir);
        _spy = new GenerationBlobSpy(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> PutAsync(string path, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        await _store.PutAsync(path, ms, "application/octet-stream");
        return path;
    }

    /// <summary>Картинка-блоб, вложение, ассет-картинка и шрифт шаблона, библиотека с деревом.</summary>
    private async Task<GenerationRequest> RequestAsync()
    {
        await PutAsync("b/sign.png", [1, 2, 3]);
        await PutAsync("b/att.pdf", [4, 5, 6]);
        await PutAsync("b/logo.png", [7]);
        await PutAsync("b/font.ttf", [8]);

        var ctx = new GenerationContext();
        ctx.Set("Подпись", JsonSerializer.SerializeToElement(new Dictionary<string, string>
            { ["$type"] = "image", ["blobPath"] = "b/sign.png", ["mimeType"] = "image/png" }));
        ctx.Set("Приложение", JsonSerializer.SerializeToElement(new Dictionary<string, string>
            { ["$type"] = "file", ["blobPath"] = "b/att.pdf", ["fileName"] = "a.pdf", ["mimeType"] = "application/pdf" }));
        ctx.Set("Номер", JsonSerializer.SerializeToElement("А-1"));

        return new GenerationRequest("= Шаблон", OutputFormat.Pdf, ctx,
            UserLibContent: "#import \"userlib/a.typ\": *",
            UserLibFiles: [new UserLibFile("a.typ", "#let a = 1")],
            TemplateAssets: new ResolvedTemplateAssets(
                [new ResolvedImageAsset("logo", "logo.png", "image/png", "b/logo.png")],
                [new ResolvedFontAsset("font.ttf", "b/font.ttf")]));
    }

    private IReadOnlyList<string> Files() => [.. Directory
        .EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
        .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Layout_IsTheDocumentedOne()
    {
        var layout = await new TypstCompileDirectory(_spy).WriteAsync(await RequestAsync(), _dir, JsonLayout.Compact, default);

        Assert.Equal(
            ["assets/att_0.pdf", "assets/img_0.png", "assets/logo.png", "data.json", "fonts/font_0.ttf",
             "systemlib.typ", "template.typ", "typeblocks.typ", "userlib.typ", "userlib/a.typ"],
            Files());
        Assert.Equal(Path.Combine(_dir, "fonts"), layout.FontsDir);
        Assert.Empty(layout.UnreadableImages);

        var data = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "data.json")))!;
        Assert.Equal("/assets/img_0.png", (string?)data["Подпись"]!["src"]);
        Assert.Equal("/assets/att_0.pdf", (string?)data["Приложение"]!["src"]);
    }

    /// <summary>Пакет пишет data.json с отступами — значения те же, что у генерации.</summary>
    [Fact]
    public async Task IndentedJson_HasTheSameValues()
    {
        await new TypstCompileDirectory(_spy).WriteAsync(await RequestAsync(), _dir, JsonLayout.Compact, default);
        var compact = await File.ReadAllTextAsync(Path.Combine(_dir, "data.json"));
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);

        await new TypstCompileDirectory(_spy).WriteAsync(await RequestAsync(), _dir, JsonLayout.Indented, default);
        var indented = await File.ReadAllTextAsync(Path.Combine(_dir, "data.json"));

        Assert.NotEqual(compact, indented);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(compact), JsonNode.Parse(indented)));
        Assert.Contains("А-1", indented);   // кириллица не скрыта — пакет читают глазами
    }

    /// <summary>Предел 100 МБ — и для ассета шаблона, и для вложения: файл больше него не пишется.</summary>
    [Fact]
    public async Task FilesOverTheLimit_AreSkipped()
    {
        var request = await RequestAsync();
        _spy.Oversize["b/logo.png"] = true;
        _spy.Oversize["b/att.pdf"] = true;

        await new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, default);

        Assert.DoesNotContain("assets/logo.png", Files());
        Assert.DoesNotContain("assets/att_0.pdf", Files());
    }

    /// <summary>Шрифт объявлен, хранилище не отдало: каталога шрифтов нет — и ключа --font-path тоже.</summary>
    [Fact]
    public async Task FontNotDownloaded_LeavesNoFontsDir()
    {
        var request = await RequestAsync();
        _spy.Refuse["b/font.ttf"] = true;

        var layout = await new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, default);

        Assert.Null(layout.FontsDir);
        Assert.False(Directory.Exists(Path.Combine(_dir, "fonts")));
    }

    /// <summary>Отмена на загрузке ассета прерывает запись — к следующему файлу она не идёт.</summary>
    [Fact]
    public async Task Cancellation_StopsAtTheCurrentFile()
    {
        var request = await RequestAsync();
        using var cts = new CancellationTokenSource();
        _spy.BeforeRead["b/logo.png"] = cts.Cancel;
        var reads = 0;
        _spy.BeforeRead["b/font.ttf"] = () => reads++;

        // Подделка токен не слушает — отмену видит чтение тела файла, как у настоящего хранилища.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, cts.Token));
        Assert.Equal(0, reads);
    }

    /// <summary>Картинка, которой хранилище не отдало, называется адресом поля (ТЗ CORE-37.2).</summary>
    [Fact]
    public async Task UnreadableImage_IsNamedByField()
    {
        var request = await RequestAsync();
        _spy.Refuse["b/sign.png"] = true;

        var layout = await new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, default);

        Assert.Equal(["Подпись"], layout.UnreadableImages);
    }
}
