using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Maintenance;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сборщик осиротевших объектов хранилища (issue #741).
///
/// <para>Проверяем ровно те свойства, потеря любого из которых делает уборку вредной: файл со
/// ссылкой остаётся жив (в том числе когда ссылка спрятана в JSONB реквизитов — а это единственное
/// место, где живёт путь обычного вложения), файл без ссылок уходит и из хранилища, и из реестра,
/// свежая загрузка не трогается, а подсчёт ничего не удаляет.</para>
///
/// <para>Отдельно — тест на расхождение с разовым сбором реестра: оба ищут пути в одних и тех же
/// местах, и разъехавшись, они начнут считать живое сиротой. Это тот случай, когда защиту надо
/// ставить не на текущее поведение, а на согласованность двух списков.</para>
/// </summary>
[Collection("Integration")]
public class OrphanBlobCleanupTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));
    private readonly Guid _userId = Guid.NewGuid();

    private async Task<string> UploadAsync(string fileName)
    {
        using var scope = fixture.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        return await blob.UploadAsync(fileName, Content("содержимое"), "application/pdf");
    }

    private async Task<OrphanBlobReport> RunAsync(bool dryRun, int? minAgeHours = 0)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrphanBlobCleanup>().RunAsync(dryRun, minAgeHours);
    }

    private bool InStorage(string path)
        => fixture.Services.GetRequiredService<FakeBlobStorage>().Exists(path);

    private async Task<bool> InRegistryAsync(string path)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobRegistry.AsNoTracking().AnyAsync(e => e.Path == path);
    }

    /// <summary>Документ качества со сканом — путь лежит в отдельной КОЛОНКЕ.</summary>
    private async Task SeedQualityDocAsync(string scanPath)
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Сертификат", "CERT", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        await m.Send(new CreateQualityDocumentCommand(
            type.Id, "Сертификат 1", J("{}"), CatalogScope.System, null, QualityDocSource.Manual,
            scanPath, "cert.pdf", "application/pdf"));
    }

    /// <summary>Документ с вложением — путь лежит ВНУТРИ JSONB реквизитов, колонки под него нет.</summary>
    private async Task SeedDocumentWithAttachmentAsync(string attachmentPath)
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", "AOSR", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));
        await m.Send(new UpdateRequisitesCommand(instance.Id, J(
            "{'Приложение': {'$type': 'file', 'blobPath': '" + attachmentPath + "',"
            + " 'fileName': 'прил.pdf', 'mimeType': 'application/pdf'}}")));
    }

    // ── Основное поведение ───────────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_RemovesUnreferencedBlob_FromStorageAndRegistry()
    {
        var orphan = await UploadAsync("забытый.pdf");

        var report = await RunAsync(dryRun: false);

        Assert.Equal(1, report.Orphans);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(0, report.Referenced);
        Assert.False(InStorage(orphan));
        Assert.False(await InRegistryAsync(orphan));
    }

    /// <summary>
    /// Скан документа качества — ровно тот файл, который жаловались терять. Пока документ жив,
    /// сборщик обязан пройти мимо.
    /// </summary>
    [Fact]
    public async Task Cleanup_KeepsBlobReferencedByColumn()
    {
        var scan = await UploadAsync("cert.pdf");
        await SeedQualityDocAsync(scan);

        var report = await RunAsync(dryRun: false);

        Assert.Equal(0, report.Orphans);
        Assert.Equal(1, report.Referenced);
        Assert.True(InStorage(scan));
    }

    /// <summary>
    /// Главный случай: путь вложения не лежит НИ В ОДНОЙ колонке — только внутри JSONB реквизитов,
    /// куда его кладёт клиент. Ищи сборщик держателей по списку таблиц — он бы этот файл удалил.
    /// </summary>
    [Fact]
    public async Task Cleanup_KeepsBlobReferencedFromRequisitesJson()
    {
        var attachment = await UploadAsync("прил.pdf");
        await SeedDocumentWithAttachmentAsync(attachment);

        var report = await RunAsync(dryRun: false);

        Assert.Equal(0, report.Orphans);
        Assert.Equal(1, report.Referenced);
        Assert.True(InStorage(attachment));
    }

    [Fact]
    public async Task Cleanup_SeparatesReferencedFromOrphan()
    {
        var referenced = await UploadAsync("cert.pdf");
        var orphan = await UploadAsync("забытый.pdf");
        await SeedQualityDocAsync(referenced);

        var report = await RunAsync(dryRun: false);

        Assert.Equal(2, report.Registered);
        Assert.Equal(1, report.Referenced);
        Assert.Equal(1, report.Deleted);
        Assert.True(InStorage(referenced));
        Assert.False(InStorage(orphan));
    }

    /// <summary>
    /// Возрастной порог. Файл попадает в хранилище раньше, чем ссылка на него сохраняется в
    /// документ: клиент грузит, получает путь и лишь потом отправляет форму. В эту щель сборщик без
    /// порога отобрал бы у человека то, что он прямо сейчас прикрепляет.
    /// </summary>
    [Fact]
    public async Task Cleanup_SkipsFreshUpload()
    {
        var fresh = await UploadAsync("только-что.pdf");

        var report = await RunAsync(dryRun: false, minAgeHours: null); // умолчание — сутки

        Assert.Equal(24, report.MinAgeHours);
        Assert.Equal(0, report.Orphans);
        Assert.Equal(1, report.TooYoung);
        Assert.True(InStorage(fresh));
    }

    [Fact]
    public async Task DryRun_CountsButDeletesNothing()
    {
        var orphan = await UploadAsync("забытый.pdf");

        var report = await RunAsync(dryRun: true);

        Assert.Equal(1, report.Orphans);
        Assert.Equal(0, report.Deleted);
        Assert.True(report.Bytes > 0);
        Assert.Contains("забытый.pdf", report.Sample);
        Assert.True(InStorage(orphan));
        Assert.True(await InRegistryAsync(orphan));
    }

    /// <summary>Повторный прогон на убранной базе находит ноль — уборка идемпотентна.</summary>
    [Fact]
    public async Task Cleanup_IsIdempotent()
    {
        await UploadAsync("забытый.pdf");
        await RunAsync(dryRun: false);

        var second = await RunAsync(dryRun: false);

        Assert.Equal(0, second.Orphans);
        Assert.Equal(0, second.Registered);
    }

    /// <summary>
    /// Запись реестра пережила свой файл (ручная уборка бакета, восстановление копии). Такую
    /// сборщик обязан снять — иначе она остаётся навсегда и каждый прогон будет считать её сиротой.
    /// </summary>
    [Fact]
    public async Task Cleanup_RemovesRegistryEntry_WhenBlobAlreadyGone()
    {
        var path = await UploadAsync("пропал.pdf");
        // Удаляем ТОЛЬКО из хранилища, минуя обёртку: запись реестра остаётся.
        await fixture.Services.GetRequiredService<FakeBlobStorage>().DeleteAsync(path);

        var report = await RunAsync(dryRun: false);

        Assert.Equal(1, report.Missing);
        Assert.Equal(0, report.Bytes);
        Assert.False(await InRegistryAsync(path));
    }

    // ── Отчёт не должен обещать больше, чем сделано ──────────────────────────────

    /// <summary>
    /// Освобождённые байты считаются ЗА УДАЛЁННОЕ, а не за намеченное.
    ///
    /// <para>Складывались они раньше до цикла удаления, а сбой каждого удаления при этом
    /// проглатывался — и отчёт получался «удалено 0, освобождено 1,2 ГБ», зелёной строкой на
    /// экране. Ровно то, от чего предостерегает комментарий у самого <c>catch</c>.</para>
    /// </summary>
    [Fact]
    public async Task Cleanup_StorageRefusesDelete_ReportsNothingFreed()
    {
        var referenced = await UploadAsync("cert.pdf");
        await SeedQualityDocAsync(referenced);   // живой объект — чтобы проба сказала «связь есть»
        await UploadAsync("забытый.pdf");
        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();

        OrphanBlobReport report;
        try
        {
            // Размеры отдаём, а удаление роняем: сбой ровно на том шаге, где раньше терялась правда.
            report = await RunAsync(dryRun: true);
            Assert.True(report.Bytes > 0);       // подсчёт всё ещё показывает вес
            storage.FailDeletes = true;
            report = await RunAsync(dryRun: false);
        }
        finally { storage.FailDeletes = false; }

        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Bytes);
    }

    /// <summary>
    /// Хранилище молчит — уборка не идёт.
    ///
    /// <para>Различить «объекта нет» и «до хранилища не достучаться» по ответу нельзя: MinIO на
    /// любой отказ отдаёт то же самое. Прими сборщик молчание за отсутствие — он снял бы записи
    /// реестра у ЖИВЫХ файлов, а без записи файл не отдаётся и больше никогда не попадётся этой же
    /// уборке. То есть данные не удалялись бы, но исчезали.</para>
    /// </summary>
    [Fact]
    public async Task Cleanup_StorageUnreachable_RefusesToRun()
    {
        var referenced = await UploadAsync("cert.pdf");
        await SeedQualityDocAsync(referenced);
        var orphan = await UploadAsync("забытый.pdf");
        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();

        OrphanBlobReport report;
        try
        {
            storage.Offline = true;
            report = await RunAsync(dryRun: false);
        }
        finally { storage.Offline = false; }

        Assert.True(report.StorageUnreachable);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Bytes);
        Assert.True(await InRegistryAsync(orphan));   // запись цела — прогон можно повторить
        Assert.True(InStorage(orphan));
    }

    // ── Согласованность со сбором реестра ────────────────────────────────────────

    /// <summary>
    /// Сбор реестра (issue #672) и поиск живых ссылок смотрят в одни и те же места. Разъедься эти
    /// два списка — сборщик посчитал бы сиротой путь, который сбор считает живым, то есть удалил бы
    /// работающий файл. Тест держит их вместе: сбор по чистому реестру обязан найти ровно то же
    /// множество путей, что и скан.
    /// </summary>
    [Fact]
    public async Task Scan_FindsSamePaths_AsRegistryBackfill()
    {
        var scanPath = await UploadAsync("cert.pdf");
        var attachment = await UploadAsync("прил.pdf");
        await SeedQualityDocAsync(scanPath);
        await SeedDocumentWithAttachmentAsync(attachment);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var live = await scope.ServiceProvider.GetRequiredService<LiveBlobPathScan>().RunAsync();

        // Реестр очищаем и собираем заново по данным — так он покажет, что нашёл БЫ сбор.
        await db.BlobRegistry.ExecuteDeleteAsync();
        await scope.ServiceProvider.GetRequiredService<BlobRegistryBackfill>().RunAsync();
        var collected = await db.BlobRegistry.AsNoTracking().Select(e => e.Path).ToListAsync();

        var expected = new[] { scanPath, attachment }.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, live.OrderBy(p => p, StringComparer.Ordinal).ToList());
        Assert.Equal(expected, collected.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }
}
