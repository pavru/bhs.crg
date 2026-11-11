using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Costs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия с правленным встроенным профилем распознавания — на НОВУЮ систему (issue #1570).
///
/// <para>Встроенные профили (<c>invoice</c>, <c>spec-table</c>…) каждая установка заводит при первом
/// старте под своим идентификатором. Копия системы, где «Счёт на оплату» правили, везёт его под
/// идентификатором исходной, и вставка падала на уникальности кода (<c>23505</c>) — откатывалось всё
/// восстановление. Тесты копии этого не видели: их база засеяна один раз, и профили копии и базы —
/// одни и те же строки.</para>
///
/// <para>Каждый тест начинает с того, с чего начинает новая система: пустая база, проекция типов и
/// засев встроенных профилей заново — под новыми идентификаторами.</para>
/// </summary>
[Collection("Integration")]
public class RestoreRecognitionProfileNamesakeTests(RestoreModuleTypesHost host)
    : IClassFixture<RestoreModuleTypesHost>, IAsyncLifetime
{
    private const string EditedName = "Счёт на оплату (правлено исходной системой)";

    public Task InitializeAsync() => FreshSystemAsync();
    // Уборка и за собой: профили сброс базы не чистит, и правленные встроенные под чужими
    // идентификаторами достались бы следующему классу на той же базе (ревью PR #1896).
    public Task DisposeAsync() => FreshSystemAsync();

    [Fact]
    public async Task Копия_с_правленным_профилем_ложится_на_заново_засеянную_систему()
    {
        var copiedId = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var startedId = await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode);
        Assert.NotEqual(copiedId, startedId);
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        // Один профиль с кодом — под идентификатором КОПИИ и в её редакции.
        Assert.Equal(copiedId, await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode));
        Assert.Equal(EditedName, await ProfileNameAsync(copiedId));
        Assert.Contains(report.Warnings, w => w.Contains("этой системы заменён профилем из копии"));
    }

    [Fact]
    public async Task Ссылки_наборов_этой_системы_переходят_на_профиль_из_копии()
    {
        var copiedInvoice = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var copiedTable = await EditProfileAsync(IdRecognitionProfiles.SpecificationTableCode, "Спецификация (правлено)");
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var hereInvoice = await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode);
        var hereTable = await ProfileIdAsync(IdRecognitionProfiles.SpecificationTableCode);
        var fileId = await FileAsync(file =>
        {
            file.SetRecognitionProfiles(JsonSerializer.Serialize(
                new Dictionary<string, Guid> { [nameof(RecognitionProfileKind.Invoice)] = hereInvoice }));
            file.SetGrouping(GroupingWith(hereTable));
        });
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        var (profiles, grouping) = await FileRefsAsync(fileId);
        Assert.Contains(copiedInvoice.ToString(), profiles);
        Assert.Contains(copiedTable.ToString(), grouping);
        Assert.DoesNotContain(hereInvoice.ToString(), profiles);
        Assert.DoesNotContain(hereTable.ToString(), grouping);
        Assert.Contains(report.Warnings, w => w.Contains("наборы данных этой системы (1) переведены"));
    }

    [Fact]
    public async Task Держатель_которого_не_переписать_останавливает_восстановление_и_назван()
    {
        var copiedId = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var hereId = await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode);
        // Карта профилей переписывается — а сырьё счёта восстановление не трогает.
        var fileId = await FileAsync(file =>
        {
            file.SetRecognitionProfiles(JsonSerializer.Serialize(
                new Dictionary<string, Guid> { [nameof(RecognitionProfileKind.Invoice)] = hereId }));
            file.SetInvoiceRawData($$"""{"profile":"{{hereId}}"}""");
        });
        var report = await ImportAsync(copy);

        Assert.False(report.Success);
        Assert.Contains(report.Warnings, w => w.Contains($"«{EditedName}»") && w.Contains("ссылается")
                                              && w.Contains("public.dataset_files.InvoiceRawData"));
        // Транзакция откатилась: здешний профиль на месте, и перепись карты — тоже назад.
        Assert.Equal(hereId, await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode));
        Assert.NotEqual(copiedId, hereId);
        Assert.Contains(hereId.ToString(), (await FileRefsAsync(fileId)).Profiles);
    }

    [Fact]
    public async Task Правленный_здешний_тёзка_заменяется_с_названной_потерей()
    {
        var copiedId = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var copy = await ExportAsync();

        await FreshSystemAsync();
        await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, "Счёт на оплату (правлено здесь)");
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal(copiedId, await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode));
        Assert.Contains(report.Warnings, w => w.Contains("(правлено здесь)") && w.Contains("правка профиля на этой системе потеряна"));
    }

    [Fact]
    public async Task Копия_на_ту_же_систему_тёзок_не_находит()
    {
        var id = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var report = await ImportAsync(await ExportAsync());

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal(id, await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("заменён профилем из копии"));
    }

    [Fact]
    public async Task Нетронутый_встроенный_из_копии_даёт_здешнему_свой_идентификатор()
    {
        // Профиль не правлен: восстановление его не кладёт, но наборы копии ссылаются на него.
        var copiedTable = await ProfileIdAsync(IdRecognitionProfiles.SpecificationTableCode);
        var copiedFile = await FileAsync(file => file.SetGrouping(GroupingWith(copiedTable)));
        var copy = await ExportAsync(BackupScope.Full);
        Assert.Contains(copy.DataSetFiles ?? [], f => f.Id == copiedFile);

        await FreshSystemAsync();
        var hereTable = await ProfileIdAsync(IdRecognitionProfiles.SpecificationTableCode);
        var hereName = await ProfileNameAsync(hereTable);
        var hereFile = await FileAsync(file => file.SetGrouping(GroupingWith(hereTable)));
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        // Профиль один — под идентификатором копии, в здешней (заводской) редакции.
        Assert.Equal(copiedTable, await ProfileIdAsync(IdRecognitionProfiles.SpecificationTableCode));
        Assert.Equal(hereName, await ProfileNameAsync(copiedTable));
        // На него указывают и набор из копии, и набор этой системы.
        Assert.Contains(copiedTable.ToString(), (await FileRefsAsync(copiedFile)).Grouping);
        Assert.Contains(copiedTable.ToString(), (await FileRefsAsync(hereFile)).Grouping);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("заменён профилем из копии"));
    }

    [Fact]
    public async Task Ссылка_заглавными_тоже_переводится()
    {
        var copiedTable = await EditProfileAsync(IdRecognitionProfiles.SpecificationTableCode, "Спецификация (правлено)");
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var hereTable = await ProfileIdAsync(IdRecognitionProfiles.SpecificationTableCode);
        var fileId = await FileAsync(file => file.SetGrouping(GroupingWith(hereTable, upper: true)));
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        var grouping = (await FileRefsAsync(fileId)).Grouping;
        Assert.Contains(copiedTable.ToString(), grouping);
        Assert.DoesNotContain(hereTable.ToString(), grouping, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Набор_из_копии_переведённым_не_считается()
    {
        var copiedId = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var fileId = await FileAsync(file => file.SetRecognitionProfiles(InvoiceMap(copiedId)));
        var copy = await ExportAsync(BackupScope.Full);
        Assert.Contains(copy.DataSetFiles ?? [], f => f.Id == fileId);

        // Та же система после пересева: набор из копии привязан к здешнему тёзке.
        await ReseedProfilesAsync();
        var hereId = await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode);
        await SetFileProfilesAsync(fileId, InvoiceMap(hereId));
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(copiedId.ToString(), (await FileRefsAsync(fileId)).Profiles);
        Assert.Contains(report.Warnings, w => w.Contains("заменён профилем из копии"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("переведены"));
    }

    [Fact]
    public async Task Обмен_кодами_с_профилем_копии_не_падает_на_индексе()
    {
        var hereId = await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var exported = await ExportAsync();
        var invoice = exported.RecognitionProfiles!.Single(p => p.Id == hereId);
        // Копия уносит здешний профиль под другим кодом, а его код отдаёт своему профилю.
        var copy = exported with
        {
            RecognitionProfiles =
            [
                // Новичок — первым: самый неудобный порядок для одного сохранения. Сегодня EF и без
                // снятия кода ставит обновление раньше вставки; тест закрепляет итог, а не его порядок.
                invoice with { Id = Guid.NewGuid(), Name = "Счёт на оплату (новая редакция)" },
                .. exported.RecognitionProfiles!.Where(p => p.Id != hereId),
                invoice with { Code = "invoice-прежний" },
            ],
        };
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.NotEqual(hereId, await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode));
        Assert.Equal(hereId, await ProfileIdAsync("invoice-прежний"));
    }

    [Fact]
    public async Task Упоминание_в_истории_держателем_не_считается()
    {
        await EditProfileAsync(CostsRecognitionProfiles.InvoiceCode, EditedName);
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var hereId = await ProfileIdAsync(CostsRecognitionProfiles.InvoiceCode);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"""
                INSERT INTO notifications ("Id", "Severity", "Title", "Message", "CreatedAt", "UpdatedAt")
                VALUES ({Guid.NewGuid()}, 'Info', 'Распознавание', {"Профиль " + hereId + " применён"}, now(), now())
                """);
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Новая система: пустая база, проекция типов и засев встроенных профилей — как при первом старте.
    /// Профили сброс базы не чистит (конфигурация), поэтому стираются здесь: иначе засев их узнал бы,
    /// и идентификаторы совпали бы с копией.
    /// </summary>
    private async Task FreshSystemAsync()
    {
        await host.ResetDatabaseAsync();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.ProjectModuleTypesAsync();
        await ReseedProfilesAsync();
    }

    /// <summary>Встроенные профили — заново, под новыми идентификаторами.</summary>
    private async Task ReseedProfilesAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.RecognitionProfiles.ExecuteDeleteAsync();
        await RecognitionProfileSeeder.SeedAsync(
            db, scope.ServiceProvider.GetRequiredService<RecognitionProfileCatalog>());
    }

    /// <summary>Правка встроенного профиля пользователем: профиль становится «правленным».</summary>
    private async Task<Guid> EditProfileAsync(string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.RecognitionProfiles.SingleAsync(p => p.Code == code);
        profile.Update(name, profile.Fields, profile.RowColumns, profile.Shape);
        await db.SaveChangesAsync();
        Assert.True(profile.IsModified);
        return profile.Id;
    }

    private async Task<Guid> ProfileIdAsync(string code)
    {
        using var scope = host.Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RecognitionProfiles
            .Where(p => p.Code == code).Select(p => p.Id).ToListAsync();
        return ids.Count == 1 ? ids[0] : throw new Xunit.Sdk.XunitException($"профилей «{code}» — {ids.Count}");
    }

    private async Task<string> ProfileNameAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RecognitionProfiles
            .Where(p => p.Id == id).Select(p => p.Name).SingleAsync();
    }

    private async Task<Guid> FileAsync(Action<DataSetFile> setUp)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = DataSetFile.Create("Счета поставщика.pdf", DataSetFormat.Pdf, "datasets/test/invoices.pdf",
            CatalogScope.System, null);
        setUp(file);
        db.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private async Task<(string Profiles, string Grouping)> FileRefsAsync(Guid fileId)
    {
        using var scope = host.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<DataSetFile>()
            .AsNoTracking().SingleAsync(f => f.Id == fileId);
        return (file.RecognitionProfiles ?? "", file.Grouping ?? "");
    }

    private static string GroupingWith(Guid profileId, bool upper = false) => $$"""
        {"Groups":[{"Kind":0,"Code":"ЭОМ.С","Name":"Спецификация","Pages":[],"Id":"{{Guid.NewGuid()}}","ProfileId":"{{(upper ? profileId.ToString().ToUpperInvariant() : profileId.ToString())}}"}],"ManuallyEdited":false}
        """;

    private static string InvoiceMap(Guid profileId) => JsonSerializer.Serialize(
        new Dictionary<string, Guid> { [nameof(RecognitionProfileKind.Invoice)] = profileId });

    private async Task SetFileProfilesAsync(Guid fileId, string map)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.Set<DataSetFile>().SingleAsync(f => f.Id == fileId);
        file.SetRecognitionProfiles(map);
        await db.SaveChangesAsync();
    }

    /// <summary>Манифест настройки этой системы — так, как его кладёт в архив сама копия.</summary>
    private async Task<BackupManifest> ExportAsync(BackupScope backupScope = BackupScope.Configuration)
    {
        using var scope = host.Services.CreateScope();
        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(backupScope);
        await using var _zipHandle = zip;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        await using var entry = archive.GetEntry("manifest.json")!.Open();
        return (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;
    }

    private async Task<RestoreReport> ImportAsync(BackupManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var w = zip.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(w, manifest);
        }
        ms.Position = 0;
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(ms);
    }
}
