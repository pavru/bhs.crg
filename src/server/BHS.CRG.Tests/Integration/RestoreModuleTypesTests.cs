using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Восстановление копии на НОВУЮ систему с модулем счетов (issue #1400) — переезд на другой сервер.
///
/// <para>Найдено репетицией выпуска, двумя отказами одного места. Первый: копия, снятая без модуля,
/// восстанавливалась, а тип «Счёт на оплату» появлялся только после перезапуска API — проекция типов
/// модулей идёт при старте, а роль справочника организаций, на которую тип опирается, назначает как раз
/// восстановление. Второй: копия системы С модулем не восстанавливалась вовсе — новая система при старте
/// заводит «СтатьяВнеСтроек» под своим идентификатором, и вставка того же кода из копии падала на
/// уникальности. Тесты копии ни того, ни другого не видели: после сброса тестовой базы проекция заново
/// не запускается, и встречи «заведённое стартом против копии» у них не бывает.</para>
///
/// <para>Каждый тест начинает с того, с чего начинает новая система: пустая база, затем проекция
/// типов — тот же шаг, что делает старт.</para>
/// </summary>
[Collection("Integration")]
public class RestoreModuleTypesTests(RestoreModuleTypesHost host) : IClassFixture<RestoreModuleTypesHost>, IAsyncLifetime
{
    public Task InitializeAsync() => FreshSystemAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Копия_без_модуля_тип_счёта_появляется_без_перезапуска()
    {
        // Исходная система — без модуля, со справочниками ядра по прежним именам (как 1.0.0).
        await TypeAsync(TestDirectories.OrganizationCode, "Организация");
        await TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        var source = await ExportAsync();
        var copy = source with
        {
            DocumentTypes = source.DocumentTypes.Where(t => t.Module != CostsModule.ModuleCode).ToArray(),
            KnowsDirectoryRoles = null,
        };
        Assert.DoesNotContain(copy.DocumentTypes, t => t.Code == CostsRecordTypes.InvoiceCode);

        await FreshSystemAsync();
        Assert.Null(await TypeIdAsync(CostsRecordTypes.InvoiceCode));
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.NotNull(await TypeIdAsync(CostsRecordTypes.InvoiceCode));
    }

    [Fact]
    public async Task Копия_с_модулем_ложится_на_тип_заведённый_стартом()
    {
        var copiedId = await TypeIdAsync(CostsRecordTypes.ArticleCode);
        Assert.NotNull(copiedId);
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var startedId = await TypeIdAsync(CostsRecordTypes.ArticleCode);
        Assert.NotEqual(copiedId, startedId);
        var report = await ImportAsync(copy);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        // Один тип с кодом — и под идентификатором КОПИИ: на него ссылаются её данные.
        Assert.Equal(copiedId, await TypeIdAsync(CostsRecordTypes.ArticleCode));
        Assert.Contains(report.Warnings, w => w.Contains("заменён одноимённым из копии"));
    }

    [Fact]
    public async Task Занятый_тёзка_останавливает_восстановление_и_назван()
    {
        var copy = await ExportAsync();

        await FreshSystemAsync();
        var startedId = (await TypeIdAsync(CostsRecordTypes.ArticleCode))!.Value;
        // Здешний тип уже держат данные этой системы — дочерний тип.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Create("Подстатья", "Подстатья", DocumentTypeKind.Composite, startedId,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared));
            await db.SaveChangesAsync();
        }
        var report = await ImportAsync(copy);

        Assert.False(report.Success);
        Assert.Contains(report.Warnings, w => w.Contains("здешний уже занят") && w.Contains("document_types.ParentId"));
        Assert.Equal(startedId, await TypeIdAsync(CostsRecordTypes.ArticleCode));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Новая система с модулем: пустая база и проекция типов, как при первом старте.</summary>
    private async Task FreshSystemAsync()
    {
        await host.ResetDatabaseAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
    }

    private async Task TypeAsync(string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        await types.AddAsync(DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared));
        await types.SaveChangesAsync();
    }

    private async Task<Guid?> TypeIdAsync(string code)
    {
        using var scope = host.Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentTypes
            .Where(t => t.Code == code).Select(t => t.Id).ToListAsync();
        return ids.Count switch { 0 => null, 1 => ids[0], _ => throw new Xunit.Sdk.XunitException($"типов «{code}» — {ids.Count}") };
    }

    /// <summary>Манифест настройки этой системы — так, как его кладёт в архив сама копия.</summary>
    private async Task<BackupManifest> ExportAsync()
    {
        using var scope = host.Services.CreateScope();
        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync();
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

/// <summary>
/// Хост «новая система со счетами»: <c>Modules__Enabled=id,costs</c> и своя база.
///
/// <para>Своя база — по той же причине, что у <see cref="ModuleSchemaHost" />: тесты стирают типы и
/// восстанавливают копии поверх, а общая база хоста строк счёта держит засев соседних классов.</para>
/// </summary>
public sealed class RestoreModuleTypesHost : IntegrationTestFixture
{
    private static string ConnectionString { get; } = TestDatabases.ConnectionString("restoretypes");

    protected override string HostConnectionString => ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
