using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Владелец файла хранилища (ТЗ CORE-37.2, WORK-39): общий адрес ядра и генерация выдают только файлы
/// ядра, порт модуля — только свои.
///
/// <para>Модуль <c>work</c> изолирует отчёты построчно, и фото чужого отчёта не должно открываться ни
/// одним путём, кроме адреса модуля с его проверкой. Здесь проверяются все остальные пути: общий адрес,
/// материализация картинки из реквизитов, порт другого модуля, удаление документа с вписанным путём.
/// Модуль <c>work</c> в поставке ещё не собран — его порт строится тем же классом, что строит хост.</para>
/// </summary>
[Collection("Integration")]
public class BlobOwnerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Costs = "costs";
    private const string Work = "work";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IBlobStorage Blobs => fixture.Services.GetRequiredService<IBlobStorage>();

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private Task<string> PutAsync(string owner, string name = "скан.png") =>
        Blobs.UploadAsync(name, Content("файл " + owner), "image/png", owner);

    [Fact]
    public async Task Файл_модуля_общим_адресом_отвечает_404_как_незнакомый_путь()
    {
        var module = await PutAsync(Costs);
        var core = await Blobs.UploadAsync("вложение.png", Content("файл ядра"), "image/png");
        var client = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.FilesUse);

        var foreign = await client.GetAsync($"/api/attachments?path={Uri.EscapeDataString(module)}");
        var unknown = await client.GetAsync(
            $"/api/attachments?path={Uri.EscapeDataString("bhs-crg/2026.01.02/" + Guid.NewGuid() + "_x.png")}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        // Ответ на файл модуля неотличим от ответа на путь, которого нет: разница сама была бы сведением.
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());

        var own = await client.GetAsync($"/api/attachments?path={Uri.EscapeDataString(core)}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("файл ядра", await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Без_права_на_файлы_общий_адрес_отвечает_403()
    {
        var core = await Blobs.UploadAsync("вложение.png", Content("файл ядра"), "image/png");
        var client = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead);

        var answer = await client.GetAsync($"/api/attachments?path={Uri.EscapeDataString(core)}");

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    /// <summary>
    /// Путь файла модуля, вписанный в поле-картинку реквизитов, в документ не попадает, а поле
    /// называется. Тот же путь у файла ядра материализуется — проверка отличает владельца, а не путь.
    /// </summary>
    [Fact]
    public async Task Путь_файла_модуля_в_реквизитах_не_материализуется_и_поле_называется()
    {
        var module = await PutAsync(Costs);
        var core = await Blobs.UploadAsync("подпись.png", Content("подпись"), "image/png");
        var dir = Directory.CreateTempSubdirectory("blob-owner-").FullName;
        try
        {
            var unreadable = new List<string>();
            var json = await TypstImageMaterializer.MaterializeAsync(
                new Dictionary<string, object?>
                {
                    ["Скан"] = new Dictionary<string, object?> { ["$type"] = "image", ["blobPath"] = module },
                    ["Подпись"] = new Dictionary<string, object?> { ["$type"] = "image", ["blobPath"] = core },
                },
                dir, Blobs, unreadable: unreadable);

            Assert.Equal(["Скан"], unreadable);
            var node = JsonNode.Parse(json)!;
            // Узел файла модуля остался ссылкой — в документ ничего не легло; картинка ядра стала файлом.
            Assert.Equal(module, (string?)node["Скан"]!["blobPath"]);
            Assert.NotNull(node["Подпись"]!["src"]);
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "assets")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Генерация с непрочитанной картинкой отказывает и называет поле — даже когда шаблон картинку не
    /// выводит и собрался бы: PDF без подписи, выданный молча, был бы отказом, переодетым в результат.
    /// Та же картинка ядра документ не останавливает — отказ про владельца, а не про шаблон.
    /// </summary>
    [Fact]
    public async Task Генерация_с_картинкой_файла_модуля_отказывает_и_называет_поле()
    {
        var module = await PutAsync(Costs);
        var core = await Blobs.UploadAsync("подпись.png", Content("подпись"), "image/png");
        var generator = new TypstGenerator(Blobs, NullLogger<TypstGenerator>.Instance);

        var refusal = await Assert.ThrowsAsync<UnreadableImagesException>(
            () => generator.GenerateAsync(Request(module)));

        Assert.Equal(["Подпись"], refusal.Fields);
        Assert.Contains("Подпись", refusal.Message);

        Assert.NotEmpty(await generator.GenerateAsync(Request(core)));

        static GenerationRequest Request(string blobPath)
        {
            var context = new GenerationContext();
            context.Set("Подпись", new Dictionary<string, object?> { ["$type"] = "image", ["blobPath"] = blobPath });
            return new GenerationRequest("Документ без картинок.", OutputFormat.Pdf, context);
        }
    }

    [Fact]
    public async Task Порт_модуля_не_открывает_файл_другого_модуля_и_ядра()
    {
        var costsFile = await PutAsync(Costs);
        var coreFile = await Blobs.UploadAsync("вложение.png", Content("файл ядра"), "image/png");
        IModuleBlobs work = new ModuleBlobsPort(Blobs, Work);
        IModuleBlobs costs = new ModuleBlobsPort(Blobs, Costs);

        await Assert.ThrowsAsync<NotFoundException>(() => work.OpenAsync(costsFile));
        await Assert.ThrowsAsync<NotFoundException>(() => costs.OpenAsync(coreFile));
        Assert.Null(await work.SizeAsync(costsFile));

        // Удаление чужого — не отказ (повтор удаления безобиден), но и файл остаётся на месте.
        await work.DeleteAsync(costsFile);
        await using var kept = await costs.OpenAsync(costsFile);
        Assert.Equal("файл costs", await new StreamReader(kept).ReadToEndAsync());
    }

    /// <summary>
    /// Удаление от имени ядра не трогает файл модуля: пути в реквизитах пишет клиент, и удаление
    /// документа с вписанным путём скана иначе удалило бы скан.
    /// </summary>
    [Fact]
    public async Task Удаление_от_имени_ядра_не_трогает_файл_модуля()
    {
        var module = await PutAsync(Costs);

        await Blobs.DeleteAsync(module);

        Assert.NotNull(await Blobs.GetSizeAsync(module, Costs));
    }

    /// <summary>
    /// Объект без записи в реестре модуль не удаляет: его файлы ложатся портом и значатся в реестре
    /// всегда, а путь без записи — чужая загрузка на полпути или выпавший объект. Ядро удаляет его, как
    /// прежде. «Любой» на удалении — отказ: удалять мимо владельца некому.
    /// </summary>
    [Fact]
    public async Task Модуль_не_удаляет_объект_вне_реестра_а_любой_не_удаляет_ничего()
    {
        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();
        var stray = "fake/" + BHS.CRG.Infrastructure.Storage.BlobPathShape.NewObjectName("ничей.png");
        await storage.PutAsync(stray, Content("ничей"), "image/png");

        await new ModuleBlobsPort(Blobs, Work).DeleteAsync(stray);
        Assert.True(storage.Exists(stray));

        await Assert.ThrowsAsync<ArgumentException>(() => Blobs.DeleteAsync(stray, BlobOwner.Any));
        Assert.True(storage.Exists(stray));

        await Blobs.DeleteAsync(stray);
        Assert.False(storage.Exists(stray));
    }

    [Fact]
    public async Task Копия_читает_всех_а_восстановление_возвращает_владельца()
    {
        var module = await PutAsync(Costs);

        await using (var read = await Blobs.DownloadAsync(module, BlobOwner.Any))
            Assert.Equal("файл costs", await new StreamReader(read).ReadToEndAsync());

        // Запись уцелела на этом пути с другим владельцем — восстановление вернёт владельца из копии.
        await Blobs.PutAsync(module, Content("из копии"), "image/png");
        Assert.Equal(BlobOwner.Core, await OwnerAsync(module));
        await Blobs.PutAsync(module, Content("из копии"), "image/png", Costs);
        Assert.Equal(Costs, await OwnerAsync(module));
    }

    [Fact]
    public async Task Записать_файл_от_имени_любого_нельзя()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Blobs.UploadAsync("x.png", Content("x"), "image/png", BlobOwner.Any));
    }

    private async Task<string> OwnerAsync(string path)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobRegistry.Where(e => e.Path == path).Select(e => e.Owner).SingleAsync();
    }
}
