using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Полнота бэкапа (issue #403, #833). Набор разнесён по вопросам, на которые отвечает:
///
/// <list type="bullet">
/// <item><c>BackupServiceTests.RoundTrip.cs</c> — выгрузили, стёрли, восстановили, сошлось;</item>
/// <item><c>BackupServiceTests.RestoreReport.cs</c> — что копия сообщает о неполных данных;</item>
/// <item><c>BackupServiceTests.Manifest.cs</c> — паспорт копии и оценка её веса.</item>
/// </list>
///
/// <para>Здесь — фикстура и общие помощники. Разбит так же, как сам <c>BackupService</c>
/// (issue #1021): файл открывают, когда копия сломалась, то есть в худший момент.</para>
/// </summary>
[Collection("Integration")]
public partial class BackupServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string AssetBlobPath = "assets/logo.png";
    private static readonly byte[] AssetBytes = [1, 2, 3, 4, 5];

    private BackupService Backup(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
        NullLogger<BackupService>.Instance,
        scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>());

    /// <summary>Манифест с одними нужными секциями — остальные пустые.</summary>
    private static BackupManifest ManifestWith(
        BackupDocumentType[] documentTypes,
        BackupQualityDocument[]? qualityDocuments = null) =>
        new(SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: documentTypes,
            Templates: [],
            CatalogEntities: [],
            CommonDataEntries: [],
            QualityDocuments: qualityDocuments);

    /// <summary>Собирает zip из манифеста и восстанавливает — без блобов.</summary>
    private async Task<RestoreReport> ImportManifestAsync(BackupManifest manifest)
    {
        await fixture.ResetDatabaseAsync();
        return await ImportManifestZipAsync(manifest);
    }

    /// <summary>
    /// То же, но БЕЗ очистки: для случаев, где проверяется встреча копии с уже существующими
    /// данными (связки, скан, дубль имени) — очистка снесла бы то самое, ради чего тест написан.
    /// </summary>
    private async Task<RestoreReport> ImportManifestZipAsync(BackupManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var w = entry.Open();
            await JsonSerializer.SerializeAsync(w, manifest, new JsonSerializerOptions { WriteIndented = true });
        }
        ms.Position = 0;

        using var scope = fixture.Services.CreateScope();
        return await Backup(scope).ImportAsync(ms);
    }
}
