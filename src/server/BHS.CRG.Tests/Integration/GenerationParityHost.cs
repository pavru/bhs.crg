using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Storage;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>Файлы сида в хранилище — по ним тест включает подмены шпиона.</summary>
public sealed record ParityBlobs(string Signature, string Attachment, string Logo, string Spare, string Font);

/// <summary>Документ сида: экземпляр, шаблон, метка шаблона для поиска каталога компиляции.</summary>
public sealed record ParitySeed(Guid InstanceId, Guid TemplateId, string Marker, ParityBlobs Blobs);

/// <summary>
/// Хост сторожа «пакет = вход генерации» (change core-generation-input-one-path, D6): тот же хост, что у
/// набора, но хранилище — <see cref="GenerationBlobSpy" /> над реестром. Плюс сид и обращения к
/// генерации и пакету — общие для сценариев совпадения и расхождений.
/// </summary>
public sealed class GenerationParityHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static GenerationParityHost? _shared;

    private GenerationParityHost(WebApplicationFactory<Program> factory, HttpClient client)
    {
        Factory = factory;
        Client = client;
    }

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public GenerationBlobSpy Spy => Factory.Services.GetRequiredService<GenerationBlobSpy>();

    /// <summary>Один хост на прогон: подъём хоста — секунды, а сценариев много. Убирает его фикстура
    /// вместе со своими производными фабриками.</summary>
    public static async Task<GenerationParityHost> ForAsync(IntegrationTestFixture fixture)
    {
        await Gate.WaitAsync();
        try
        {
            if (_shared is not null) return _shared;
            var factory = fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStorage>();
                services.AddSingleton(sp => new GenerationBlobSpy(new RegisteredBlobStorage(
                    sp.GetRequiredService<FakeBlobStorage>(),
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ILogger<RegisteredBlobStorage>>())));
                services.AddSingleton<IBlobStorage>(sp => sp.GetRequiredService<GenerationBlobSpy>());
            }));
            var client = await GrantedSignIn.WithPermissionsAsync(factory, "id.document.generate");
            return _shared = new GenerationParityHost(factory, client);
        }
        finally { Gate.Release(); }
    }

    // 1×1, непрозрачный пиксель — Typst его принимает, а размер не влияет ни на что.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// Тип с картинкой и вложением-PDF, шаблон с ассетом-картинкой и шрифтом, экземпляр (D6). Ассеты —
    /// уровня шаблона: системный уровень достался бы чужим тестам. «Запасной» ассет шаблон не выводит:
    /// он нужен сценарию предела размера, где генерация его не видит. Шрифт — не настоящий файл: Typst
    /// такие пропускает, а сторожу важно, что файл в каталоге есть и ключ <c>--font-path</c> назван.
    /// </summary>
    public async Task<ParitySeed> SeedAsync(bool fillRequired = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var marker = $"parity-{Guid.NewGuid():N}";
        using var scope = Factory.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        async Task<string> Upload(string name, byte[] bytes, string mime)
        {
            using var ms = new MemoryStream(bytes);
            return await blob.UploadAsync(name, ms, mime);
        }

        var blobs = new ParityBlobs(
            await Upload("Подпись.png", Png, "image/png"),
            await Upload("Приложение.pdf", TwoPagePdf(), "application/pdf"),
            await Upload("logo.png", Png, "image/png"),
            await Upload("spare.png", Png, "image/png"),
            await Upload("font.ttf", "not really a font"u8.ToArray(), "font/ttf"));

        var docType = await m.Send(new CreateDocumentTypeCommand(
            $"Акт {suffix}", $"GEN{suffix}"[..11], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""
                { "fields": [
                    { "key": "Номер", "type": "string", "required": true },
                    { "key": "Подпись", "type": "image" },
                    { "key": "Приложение", "type": "file" } ] }
                """)));

        // systemlib назван сам — тогда создание не подставит импорт блоков типов, а с ним и блоки всех
        // типов, заведённых соседними тестами. Цикл держит компилятор не меньше секунды — см.
        // CompileDirectoryWatch.
        var template = await m.Send(new CreateTemplateCommand(docType.Id, $"Шаблон {suffix}", $$"""
            // {{marker}}
            #import "systemlib.typ": *
            #let data = json("data.json")
            #let spin = range(400000).fold(0, (a, b) => a + b)
            = Акт № #data.at("Номер", default: "без номера")
            #image(data.at("Подпись").src, width: 1cm)
            #image("/assets/logo.png", width: 1cm)
            #let att = data.at("Приложение")
            #for p in range(1, att.at("pageCount", default: 1) + 1) [ Лист #p #image(att.src, page: p, width: 3cm) ]
            Контроль #spin
            """));

        await m.Send(new CreateTemplateAssetCommand(TemplateAssetScope.Template, template.Id,
            TemplateAssetKind.Image, "logo", "logo.png", "image/png", blobs.Logo, null));
        await m.Send(new CreateTemplateAssetCommand(TemplateAssetScope.Template, template.Id,
            TemplateAssetKind.Image, "spare", "spare.png", "image/png", blobs.Spare, null));
        await m.Send(new CreateTemplateAssetCommand(TemplateAssetScope.Template, template.Id,
            TemplateAssetKind.Font, "font", "font.ttf", "font/ttf", blobs.Font, "Parity Test"));

        var construction = await m.Send(new CreateConstructionCommand($"Стройка {suffix}", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, $"Раздел {suffix}"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, $"Комплект {suffix}"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var requisites = new Dictionary<string, object>
        {
            ["Подпись"] = new Dictionary<string, object> { ["$type"] = "image", ["blobPath"] = blobs.Signature, ["mimeType"] = "image/png" },
            ["Приложение"] = new Dictionary<string, object>
            {
                ["$type"] = "file", ["blobPath"] = blobs.Attachment,
                ["fileName"] = "Приложение.pdf", ["mimeType"] = "application/pdf",
            },
        };
        if (fillRequired) requisites["Номер"] = "А-17";
        await m.Send(new UpdateRequisitesCommand(instance.Id, JsonSerializer.SerializeToDocument(requisites)));

        return new ParitySeed(instance.Id, template.Id, marker, blobs);
    }

    private static byte[] TwoPagePdf()
    {
        using var pdf = new SharpPdfDocument();
        pdf.AddPage();
        pdf.AddPage();
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Выпуск документа — с каталогом компиляции, снятым по метке шаблона.</summary>
    public async Task<(HttpResponseMessage Response, CompileDirectoryWatch Dir)> GenerateAsync(ParitySeed seed)
    {
        var watch = CompileDirectoryWatch.Start(seed.Marker);
        var response = await Client.PostAsJsonAsync($"/api/generate/{seed.InstanceId}", new { format = "pdf" });
        return (response, await watch.StopAsync());
    }

    public Task<HttpResponseMessage> GenerateWithoutWatchAsync(ParitySeed seed)
        => Client.PostAsJsonAsync($"/api/generate/{seed.InstanceId}", new { format = "pdf" });

    public async Task<byte[]> DownloadPdfAsync(ParitySeed seed)
    {
        var response = await Client.GetAsync($"/api/generate/download/{seed.InstanceId}/{seed.TemplateId}/pdf");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Отладочный пакет, разобранный в словарь «путь → байты».</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> BundleAsync(ParitySeed seed, CancellationToken ct = default)
    {
        var response = await Client.GetAsync($"/api/generate/debug-bundle/{seed.InstanceId}", ct);
        Assert.True(response.IsSuccessStatusCode,
            $"Пакет не отдан: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            await using var es = entry.Open();
            using var ms = new MemoryStream();
            await es.CopyToAsync(ms, ct);
            files[entry.FullName] = ms.ToArray();
        }
        return files;
    }

    public static string? Readme(IReadOnlyDictionary<string, byte[]> bundle)
        => bundle.TryGetValue("README.txt", out var b) ? System.Text.Encoding.UTF8.GetString(b) : null;

    /// <summary>
    /// Пакет, распакованный и скомпилированный внешним Typst так, как велит его README: с
    /// <c>--font-path fonts</c> — только если README этот ключ называет.
    /// </summary>
    public static async Task<byte[]> CompileBundleAsync(IReadOnlyDictionary<string, byte[]> bundle)
    {
        var dir = Path.Combine(Path.GetTempPath(), "parity-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, bytes) in bundle)
            {
                var target = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes);
            }
            var args = new List<string> { "compile", "template.typ", "output.pdf" };
            if (Readme(bundle)?.Contains("--font-path fonts", StringComparison.Ordinal) == true)
                args.AddRange(["--font-path", "fonts"]);
            var (exit, err) = await TypstProcess.RunAsync(TypstProcess.StartInfo(dir, [.. args]), CancellationToken.None);
            Assert.True(exit == 0, $"Пакет не компилируется внешним Typst:\n{err}");
            return await File.ReadAllBytesAsync(Path.Combine(dir, "output.pdf"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Текст PDF постранично — то, что сравнивается у выпуска и пакета.</summary>
    public static IReadOnlyList<string> PageTexts(byte[] pdf)
    {
        using var doc = PigPdfDocument.Open(pdf);
        return [.. doc.GetPages().Select(p => p.Text)];
    }
}
