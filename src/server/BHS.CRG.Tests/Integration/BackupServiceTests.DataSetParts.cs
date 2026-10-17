using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Части PDF-набора, нарезанного на документы, — часть <see cref="BackupServiceTests" /> (issue #1399).
///
/// <para>Альбом по ГОСТ режется на под-PDF, по одному на документ, и путь к каждому лежит в разбиении
/// набора и в строках реестра «Документы». Копия до #1399 брала у набора только сам файл: отчёт писал
/// «восстановлено N из N», а частей в этом числе не было вовсе — после восстановления они не значились
/// в реестре файлов, и генерация молча выдавала документы без вложений. Найдено репетицией выпуска на
/// копии рабочей системы, а не тестом: тесты копии наборов брали набор без разбиения.</para>
///
/// <para>Набор режется настоящим путём — ручной правкой разбиения (<c>ApplyGroupingAsync</c>), без
/// движка распознавания: разрезание у неё то же, что у распознавания.</para>
/// </summary>
public partial class BackupServiceTests
{
    [Fact]
    public async Task FullBackup_RoundTrips_DataSetParts()
    {
        var (fileId, sourceId, parts) = await SeedSplitDataSetAsync();
        var zipBytes = await ExportFullAsync();

        using (var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            foreach (var part in parts.Keys)
                Assert.Contains(check.Entries, e => e.FullName == $"blobs/{part}");

        await WipeAsync(fileId, parts.Keys);
        var report = await ImportZipAsync(zipBytes);

        Assert.True(report.Success);
        // Части — в том же счёте, что и сам файл: файл набора плюс два документа.
        Assert.Contains("Файлы: восстановлено 3 из 3.", report.Warnings);
        await AssertPartsReadableAsync(sourceId, parts);
    }

    /// <summary>
    /// Перепись: всё, что набор записал в хранилище, копия уносит. Сверка — с реестром файлов, а не с
    /// перечнем полей: в реестр попадает каждая запись в хранилище без исключений
    /// (<c>RegisteredBlobStorage</c>), так что новое место, где набор хранит путь, но копия его не
    /// обходит, краснеет здесь, даже если о нём не знает никто.
    /// </summary>
    [Fact]
    public async Task FullBackup_CarriesEveryBlob_TheDataSetRegistered()
    {
        await SeedSplitDataSetAsync();
        List<string> registered;
        using (var scope = fixture.Services.CreateScope())
            registered = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .BlobRegistry.Select(e => e.Path).ToListAsync();
        var zipBytes = await ExportFullAsync();

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var archived = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, registered.Count);
        Assert.All(registered, path => Assert.Contains($"blobs/{path}", archived));

        // Паспорт копии и оценка веса берут пути из того же обхода — и считают части тоже.
        Assert.Equal(3, SummaryOf(zip).BlobCount);
        using var scope2 = fixture.Services.CreateScope();
        var estimate = await Backup(scope2).EstimateSizeAsync(long.MaxValue, BackupScope.Full);
        Assert.Equal(3, estimate.Variant.BlobCount);
    }

    /// <summary>
    /// Каждое из двух мест, где лежит путь части, копия обходит само по себе. В живом наборе пути в
    /// обоих совпадают, и тест круга не отличил бы копию, забывшую одно из них, от исправной.
    /// </summary>
    [Theory]
    [InlineData("grouping")]
    [InlineData("rows")]
    public async Task FullBackup_CarriesParts_FromEachPlaceAlone(string onlyIn)
    {
        var (fileId, sourceId, parts) = await SeedSplitDataSetAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (onlyIn == "grouping")
            {
                var source = await db.DataSetSources.SingleAsync(s => s.Id == sourceId);
                source.UpdateCache(source.CachedSchema, 0, null);
            }
            else
            {
                var file = await db.DataSetFiles.SingleAsync(f => f.Id == fileId);
                var grouping = GostGroupingSerialization.Parse(file.Grouping)!;
                file.SetGrouping(JsonSerializer.Serialize(grouping with
                {
                    Groups = grouping.Groups.Select(g => g with { BlobPath = null }).ToList(),
                }));
            }
            await db.SaveChangesAsync();
        }

        using var zip = new ZipArchive(new MemoryStream(await ExportFullAsync()), ZipArchiveMode.Read);
        foreach (var part in parts.Keys)
            Assert.Contains(zip.Entries, e => e.FullName == $"blobs/{part}");
    }

    /// <summary>
    /// Разбиение, которое не читается, не роняет копию всей системы, но и не молчит: набор назван в
    /// предупреждениях копии, а сам файл набора уезжает как прежде.
    /// </summary>
    [Fact]
    public async Task UnreadableGrouping_DoesNotFailBackup_AndIsNamed()
    {
        var (fileId, sourceId, _) = await SeedSplitDataSetAsync();
        string filePath;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.DataSetFiles.SingleAsync(f => f.Id == fileId);
            // Корень — массив: колонка jsonb такое принимает, разбор разбиения — нет.
            file.SetGrouping("[]");
            var source = await db.DataSetSources.SingleAsync(s => s.Id == sourceId);
            source.UpdateCache(source.CachedSchema, 0, null);
            await db.SaveChangesAsync();
            filePath = file.BlobPath;
        }

        using var zip = new ZipArchive(new MemoryStream(await ExportFullAsync()), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == $"blobs/{filePath}");
        Assert.Contains(SummaryOf(zip).Warnings ?? [], w => w.Contains("«Альбом ЭМ»") && w.Contains("разбиение на документы не прочитано"));
    }

    /// <summary>
    /// Колонка «ФайлПуть» у ЧУЖОГО источника — текст пользователя, а не путь нашего хранилища: копия
    /// его не берёт. Иначе каждая строка загруженной таблицы стала бы «файлом, которого нет в
    /// хранилище», и настоящее предупреждение о потерянном вложении утонуло бы в них.
    /// </summary>
    [Fact]
    public async Task FilePathColumn_OfOtherSource_IsNotTakenForBlob()
    {
        var (fileId, _, _) = await SeedSplitDataSetAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.DataSetFiles.SingleAsync(f => f.Id == fileId);
            db.DataSetSources.Add(file.AddSource("Чужой лист", "Лист1", "[]", 1,
                cachedData: JsonSerializer.Serialize(new[] { new Dictionary<string, string> { ["ФайлПуть"] = @"C:\Проект.pdf" } })));
            await db.SaveChangesAsync();
        }

        using var zip = new ZipArchive(new MemoryStream(await ExportFullAsync()), ZipArchiveMode.Read);
        var summary = SummaryOf(zip);
        Assert.Equal(3, summary.BlobCount);
        Assert.DoesNotContain(summary.Warnings ?? [], w => w.Contains("не оказалось в хранилище"));
    }

    /// <summary>
    /// Обход для копий, снятых до #1399: частей в них нет, а сам файл набора есть. Повторное
    /// применение разбиения режет части заново из файла — и вложения возвращаются.
    /// </summary>
    [Fact]
    public async Task BackupWithoutParts_ReapplyingGrouping_RestoresThem()
    {
        var (fileId, sourceId, parts) = await SeedSplitDataSetAsync();
        var zipBytes = WithoutEntries(await ExportFullAsync(), parts.Keys.Select(p => $"blobs/{p}").ToHashSet());

        await WipeAsync(fileId, parts.Keys);
        var report = await ImportZipAsync(zipBytes);
        Assert.True(report.Success);

        using (var scope = fixture.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var grouping = await svc.GetPagesAsync(fileId, default);
            await svc.ApplyGroupingAsync(fileId, new ApplyGroupingInput(grouping!.Groups), default);
        }

        await AssertPartsReadableAsync(sourceId, expected: null);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Тесты этого файла пишут в хранилище настоящие файлы, а сброс базы чистит только реестр — без
    /// уборки каждый прогон оставлял бы их в бакете навсегда. Флаг ставит засев: остальным тестам
    /// класса уборка не нужна. Реестр после сброса базы перечисляет ровно файлы этого теста.
    /// </summary>
    private bool storedDataSetFiles;

    private async Task DropDataSetFilesAsync()
    {
        if (!storedDataSetFiles) return;
        using var scope = fixture.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        foreach (var path in await scope.ServiceProvider.GetRequiredService<AppDbContext>().BlobRegistry.Select(e => e.Path).ToListAsync())
            await blob.DeleteAsync(path);
    }

    private static BackupSummary SummaryOf(ZipArchive zip)
    {
        using var entry = zip.GetEntry(BackupFileStore.SummaryEntryName)!.Open();
        return JsonSerializer.Deserialize<BackupSummary>(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>Альбом из четырёх листов, разрезанный на два документа. Возвращает части с их байтами.</summary>
    private async Task<(Guid FileId, Guid SourceId, Dictionary<string, byte[]> Parts)> SeedSplitDataSetAsync()
    {
        storedDataSetFiles = true;
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        using var doc = new SharpPdfDocument();
        for (var i = 0; i < 4; i++) doc.AddPage();
        using var pdf = new MemoryStream();
        doc.Save(pdf, false);
        pdf.Position = 0;
        var blobPath = await blob.UploadAsync("album.pdf", pdf, "application/pdf");

        var file = DataSetFile.Create("Альбом ЭМ", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var source = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IDataSetService>().ApplyGroupingAsync(file.Id, new ApplyGroupingInput([
            new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Документ 1", [0, 1]),
            new GostGroupingGroupDto(GostGroupKind.Document, "02-ЭМ", "Документ 2", [2, 3]),
        ]), default);

        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var part in await PartPathsAsync(scope, source.Id))
            parts[part] = await ReadAsync(blob, part);
        Assert.Equal(2, parts.Count);
        return (file.Id, source.Id, parts);
    }

    /// <summary>Пути частей так, как их видит генерация: колонка ФайлПуть реестра «Документы».</summary>
    private static async Task<List<string>> PartPathsAsync(IServiceScope scope, Guid sourceId)
    {
        var preview = await scope.ServiceProvider.GetRequiredService<IDataSetService>()
            .PreviewSourceAsync(sourceId, 50, TestAccess.All, default);
        var column = preview!.Columns.ToList().IndexOf("ФайлПуть");
        Assert.True(column >= 0, "у реестра «Документы» нет колонки ФайлПуть");
        return preview.Rows.Select(r => r[column]).ToList()!;
    }

    /// <summary>Каждая часть читается — и, если названы ожидаемые байты, совпадает с ними.</summary>
    private async Task AssertPartsReadableAsync(Guid sourceId, Dictionary<string, byte[]>? expected)
    {
        using var scope = fixture.Services.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var paths = await PartPathsAsync(scope, sourceId);
        Assert.Equal(2, paths.Count);
        foreach (var path in paths)
        {
            Assert.False(string.IsNullOrEmpty(path), "у документа нет пути к части");
            var bytes = await ReadAsync(blob, path);
            if (expected is not null) Assert.Equal(expected[path], bytes);
            else Assert.NotEmpty(bytes);
        }
    }

    private async Task<byte[]> ExportFullAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var (zip, _) = await Backup(scope).ExportAsync(BackupScope.Full);
        await using var _zipHandle = zip;
        using var ms = new MemoryStream();
        await zip.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>Чистая система: файлы набора убраны из хранилища, база и реестр файлов пусты.</summary>
    private async Task WipeAsync(Guid fileId, IEnumerable<string> parts)
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            var file = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DataSetFiles.SingleAsync(f => f.Id == fileId);
            foreach (var path in parts.Append(file.BlobPath))
                await blob.DeleteAsync(path);
        }
        await fixture.ResetDatabaseAsync();
    }

    private async Task<RestoreReport> ImportZipAsync(byte[] zipBytes)
    {
        using var scope = fixture.Services.CreateScope();
        return await Backup(scope).ImportAsync(new MemoryStream(zipBytes));
    }

    private static async Task<byte[]> ReadAsync(IBlobStorage blob, string path)
    {
        await using var stream = await blob.DownloadAsync(path);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>Тот же архив без названных записей — копия, снятая до #1399.</summary>
    private static byte[] WithoutEntries(byte[] zipBytes, HashSet<string> drop)
    {
        using (var before = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            Assert.Equal(drop.Count, before.Entries.Count(e => drop.Contains(e.FullName)));
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.Where(e => !drop.Contains(e.FullName)))
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }
        }
        return output.ToArray();
    }
}
