using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;

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
            ["assets/logo.png", "data.json", "fields/att_0.pdf", "fields/img_0.png", "fonts/font_0.ttf",
             "systemlib.typ", "template.typ", "typeblocks.typ", "userlib.typ", "userlib/a.typ"],
            Files());
        Assert.Equal(Path.Combine(_dir, "fonts"), layout.FontsDir);
        Assert.Empty(layout.UnreadableImages);

        var data = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "data.json")))!;
        Assert.Equal("/fields/img_0.png", (string?)data["Подпись"]!["src"]);
        Assert.Equal("/fields/att_0.pdf", (string?)data["Приложение"]!["src"]);
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
        Assert.DoesNotContain("fields/att_0.pdf", Files());
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

    /// <summary>
    /// Ассет шаблона не перезаписывает картинку поля и вложение (change core-template-asset-names, D1):
    /// имя ассета выбирает человек, имена файлов полей — нумерация генератора, и совпасть по пути они не
    /// должны ни при каком имени. Вложение здесь — картинка: у ассета расширение только картиночное.
    /// </summary>
    [Theory]
    [InlineData("img_0", "Подпись")]
    [InlineData("att_0", "Скан")]
    public async Task TemplateAsset_DoesNotOverwriteFieldFile(string assetName, string field)
    {
        var request = await RequestAsync();
        await PutAsync("b/scan.png", [5]);
        await PutAsync("b/intruder", [99]);
        var data = new GenerationContext();
        data.Set("Подпись", request.Context.Data["Подпись"]);
        data.Set("Скан", JsonSerializer.SerializeToElement(new Dictionary<string, string>
            { ["$type"] = "file", ["blobPath"] = "b/scan.png", ["fileName"] = "s.png", ["mimeType"] = "image/png" }));
        request = request with
        {
            Context = data,
            TemplateAssets = new ResolvedTemplateAssets(
                [new ResolvedImageAsset(assetName, "x.png", "image/png", "b/intruder")], []),
        };

        await new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, default);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "data.json")))!;
        var fieldFile = Path.Combine(_dir, ((string)json[field]!["src"]!).TrimStart('/'));
        Assert.NotEqual([99], await File.ReadAllBytesAsync(fieldFile));
        Assert.Equal([99], await File.ReadAllBytesAsync(Path.Combine(_dir, "assets", assetName + ".png")));
    }

    /// <summary>
    /// Ассет с именем не по правилу (старые данные, копия) не скачивается и не пишется — ни в каталог,
    /// ни мимо него, — а называется в раскладке: по ней генерация откажет, а README назовёт (D3).
    /// </summary>
    [Fact]
    public async Task InvalidAssetName_IsNotWritten_ButNamed()
    {
        var request = await RequestAsync();
        var reads = 0;
        _spy.BeforeRead["b/bad.png"] = () => reads++;
        await PutAsync("b/bad.png", [99]);
        const string name = "../img_0";
        request = request with
        {
            TemplateAssets = new ResolvedTemplateAssets(
                [new ResolvedImageAsset(name, "x.png", "image/png", "b/bad.png"),
                 new ResolvedImageAsset("logo", "logo.png", "image/png", "b/logo.png")], []),
        };

        var layout = await new TypstCompileDirectory(_spy).WriteAsync(request, _dir, JsonLayout.Compact, default);

        Assert.Equal([name], layout.InvalidAssetNames);
        Assert.Equal(0, reads);
        Assert.Contains("assets/logo.png", Files());
        Assert.False(File.Exists(Path.Combine(_dir, "assets", name + ".png")));
    }

    /// <summary>
    /// Генерация с таким ассетом отказывает ДО компиляции и называет его (D3) — Typst здесь не нужен:
    /// дойди дело до компилятора, тест упал бы на его поиске, а не на отказе.
    /// </summary>
    [Fact]
    public async Task Generation_WithInvalidAssetName_RefusesNamingIt()
    {
        var request = await RequestAsync();
        request = request with
        {
            TemplateAssets = new ResolvedTemplateAssets(
                [new ResolvedImageAsset("logo(2)", "x.png", "image/png", "b/logo.png")], []),
        };

        var ex = await Assert.ThrowsAsync<InvalidTemplateAssetNamesException>(
            () => new TypstGenerator(_spy, NullLogger<TypstGenerator>.Instance).GenerateAsync(request));

        Assert.Equal(["logo(2)"], ex.Names);
        Assert.Contains("«logo(2)»", ex.Message);
        Assert.Contains("загрузите заново", ex.Message);
    }

    /// <summary>Отладочный комплект с таким ассетом отдаётся: файла ассета в нём нет, README его называет (D3).</summary>
    [Fact]
    public async Task DebugBundle_WithInvalidAssetName_IsGiven_AndNamesIt()
    {
        var request = await RequestAsync();
        request = request with
        {
            TemplateAssets = new ResolvedTemplateAssets(
                [new ResolvedImageAsset("../img_0", "x.png", "image/png", "b/logo.png")], []),
        };

        var zip = await new TypstGenerator(_spy, NullLogger<TypstGenerator>.Instance)
            .DebugBundleAsync(request, BundleNotes.None);

        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith("img_0.png", StringComparison.Ordinal)
                                                    && !e.FullName.StartsWith("fields/", StringComparison.Ordinal));
        var readme = archive.GetEntry("README.txt");
        Assert.NotNull(readme);
        using var reader = new StreamReader(readme.Open());
        Assert.Contains("[invalid-asset-name] ../img_0", await reader.ReadToEndAsync());
    }
}
