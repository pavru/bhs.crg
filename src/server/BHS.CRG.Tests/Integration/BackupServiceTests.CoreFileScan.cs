using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Обход манифеста ядра по форме пути — часть <see cref="BackupServiceTests" /> (issue #1422).
///
/// <para>Копия выбирает файлы списком мест, выписанным руками, и новое место хранения пути выпадало
/// из неё молча, с отчётом «восстановлено N из N» (#1399). Теперь к списку добавляется обход: файл,
/// о поле которого список не знает, уезжает в архив, а паспорт называет поле. Держатель здесь —
/// строка в данных записи справочника вне объекта <c>{$type: file}</c>: её список не берёт, а едет
/// она в обоих составах копии.</para>
/// </summary>
public partial class BackupServiceTests
{
    private const string UnknownFields = "в полях, о которых копия не знает";
    private const string InsideText = "записаны внутри текста";

    /// <summary>
    /// Проверка срабатывает: путь в поле, которого список мест не знает, — файл в архиве, паспорт
    /// называет поле и считает файл, восстановление возвращает его на чистую систему.
    /// </summary>
    [Theory]
    [InlineData(BackupScope.Full)]
    [InlineData(BackupScope.Configuration)]
    public async Task UnknownHolder_FileIsTaken_Named_AndRestored(BackupScope scope)
    {
        var (path, bytes) = await StoreAsync("подпись.png");
        await SeedRecordWithDataAsync(new { Подпись = path });

        var zipBytes = await ExportAsync(scope);
        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            Assert.Contains(zip.Entries, e => e.FullName == $"blobs/{path}");
            var summary = SummaryOf(zip);
            Assert.Equal(1, summary.BlobCount);
            Assert.Contains(summary.Warnings ?? [], w =>
                w.Contains(UnknownFields) && w.Contains("CommonDataEntries[].Data (1)") && w.Contains("ВЗЯТЫ"));
        }

        using (var s = fixture.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(path);
        await fixture.ResetDatabaseAsync();
        var report = await ImportZipAsync(zipBytes);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        using var check = fixture.Services.CreateScope();
        Assert.Equal(bytes, await ReadAsync(check.ServiceProvider.GetRequiredService<IBlobStorage>(), path));
    }

    /// <summary>
    /// Ссылка внутри текста: точного пути из неё не извлечь, файл не берётся — но паспорт об этом
    /// говорит, а не молчит.
    /// </summary>
    [Fact]
    public async Task PathInsideText_IsNamed_ButNotTaken()
    {
        var (path, _) = await StoreAsync("приложение.pdf");
        await SeedRecordWithDataAsync(new { Примечание = $"см. файл {path} в архиве" });

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(BackupScope.Full)), ZipArchiveMode.Read);
        var summary = SummaryOf(zip);

        Assert.DoesNotContain(zip.Entries, e => e.FullName == $"blobs/{path}");
        Assert.Equal(0, summary.BlobCount);
        Assert.Contains(summary.Warnings ?? [], w =>
            w.Contains(InsideText) && w.Contains("CommonDataEntries[].Data (1)") && w.Contains("НЕ вошли"));
    }

    /// <summary>
    /// «Взяты» говорится только о файлах, которые в архиве на самом деле (ревью PR #1423). Найденный
    /// обходом путь, файла по которому в хранилище нет, считается вместе с остальными пропавшими, а
    /// фраза обхода называет, сколько взято из скольких.
    /// </summary>
    [Fact]
    public async Task UnknownHolder_MissingFile_IsNotCalledTaken()
    {
        var gone = "bhs-crg/" + BlobPathShape.NewObjectName("удалённая подпись.png");
        await SeedRecordWithDataAsync(new { Подпись = gone });

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(BackupScope.Full)), ZipArchiveMode.Read);
        var warnings = SummaryOf(zip).Warnings ?? [];

        Assert.Contains(warnings, w => w.Contains(UnknownFields) && w.Contains("ВЗЯТЫ 0 из 1"));
        Assert.DoesNotContain(warnings, w => w.Contains("Файлы в копию ВЗЯТЫ"));
        Assert.Contains(warnings, w => w.Contains("не оказалось в хранилище: 1 из 1"));
    }

    /// <summary>
    /// Путь списка другой формы — с адресом сервера — не отменяет точного пути к тому же файлу из
    /// другого поля (ревью PR #1423). Ключ у них один, но скачать по первому нечего: файл приезжает по
    /// второму.
    /// </summary>
    [Fact]
    public async Task ExactPath_IsTaken_WhenListHoldsOtherSpellingOfSameFile()
    {
        var (path, bytes) = await StoreAsync("скан.pdf");
        await SeedRecordWithDataAsync(new Dictionary<string, object>
        {
            ["Скан"] = new Dictionary<string, string> { ["$type"] = "file", ["blobPath"] = "https://files.example/" + path },
            ["Подпись"] = path,
        });

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(BackupScope.Full)), ZipArchiveMode.Read);

        var entry = zip.GetEntry($"blobs/{path}");
        Assert.NotNull(entry);
        using var read = new MemoryStream();
        await using (var from = entry.Open()) await from.CopyToAsync(read);
        Assert.Equal(bytes, read.ToArray());
        Assert.Contains(SummaryOf(zip).Warnings ?? [], w => w.Contains(UnknownFields) && w.Contains("Файлы в копию ВЗЯТЫ"));
    }

    /// <summary>
    /// Ссылка на один файл, повторённая в поле — в двух строках и дважды в одной, — один файл, а не
    /// три (ревью PR #1423).
    /// </summary>
    [Fact]
    public async Task PathInsideText_CountsEachFileOnce()
    {
        var (path, _) = await StoreAsync("приложение.pdf");
        await SeedRecordWithDataAsync(new { Первое = $"см. {path}", Второе = $"см. {path} и {path}" });

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(BackupScope.Full)), ZipArchiveMode.Read);

        Assert.Contains(SummaryOf(zip).Warnings ?? [], w =>
            w.Contains(InsideText) && w.Contains("CommonDataEntries[].Data (1)") && w.Contains("эти файлы (1)"));
    }

    /// <summary>
    /// Нет шума: держатели, которые список знает, — вложение с оригиналом картинки, набор с частями
    /// (разбиение, строки реестра, образцы колонок), — и журнал с путём к давно удалённому файлу.
    /// Обход не говорит ни слова, и «не оказалось в хранилище» тоже нет: журнал он не смотрит.
    /// </summary>
    [Fact]
    public async Task KnownHolders_AndJournal_GiveNoScanWarnings()
    {
        await SeedSplitDataSetAsync();
        var (image, _) = await StoreAsync("схема.png");
        var (original, _) = await StoreAsync("схема-оригинал.png");
        await SeedRecordWithDataAsync(new Dictionary<string, object>
        {
            ["Схема"] = new Dictionary<string, string>
            {
                ["$type"] = "image", ["blobPath"] = image, ["originalBlobPath"] = original,
            },
        });
        using (var s = fixture.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ActivityRecords.Add(ActivityRecord.Create("test.removed", null, "Тест",
                before: BlobPathShape.NewObjectName("удалённый.pdf").Insert(0, "bhs-crg/")));
            await db.SaveChangesAsync();
        }

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(BackupScope.Full)), ZipArchiveMode.Read);
        var summary = SummaryOf(zip);

        Assert.Equal(5, summary.BlobCount);
        Assert.True(summary.Warnings is null, string.Join("; ", summary.Warnings ?? []));
    }

    /// <summary>
    /// Оценка веса получает тот же набор путей и тот же паспорт с предупреждениями обхода, что и
    /// настоящая копия: при незнакомом держателе расхождение не выходит за допуск.
    /// </summary>
    [Theory]
    [InlineData(BackupScope.Full)]
    [InlineData(BackupScope.Configuration)]
    public async Task Estimate_MatchesArchive_WithUnknownHolder(BackupScope scope)
    {
        var (taken, _) = await StoreAsync("подпись.png");
        var (inText, _) = await StoreAsync("приложение.pdf");
        await SeedRecordWithDataAsync(new { Подпись = taken, Примечание = $"см. {inText}" });

        BackupSizeEstimate estimate;
        using (var s = fixture.Services.CreateScope())
            estimate = await Backup(s).EstimateSizeAsync(long.MaxValue, scope);
        var zipBytes = await ExportAsync(scope);
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var summary = SummaryOf(zip);

        Assert.Equal(2, summary.Warnings?.Count(w => w.Contains(UnknownFields) || w.Contains(InsideText)));
        Assert.Equal(summary.BlobCount, estimate.Variant.BlobCount);
        var diff = Math.Abs(estimate.Variant.TotalBytes - zipBytes.LongLength);
        Assert.True(diff < 256, $"оценка {estimate.Variant.TotalBytes} против архива {zipBytes.LongLength} (разница {diff} байт)");
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Файл в хранилище настоящим путём — той формы, что строит приложение.</summary>
    private async Task<(string Path, byte[] Bytes)> StoreAsync(string fileName)
    {
        storedDataSetFiles = true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { fileName, at = Guid.NewGuid() });
        using var scope = fixture.Services.CreateScope();
        var path = await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
            .UploadAsync(fileName, new MemoryStream(bytes), "application/octet-stream");
        Assert.True(BlobPathShape.IsExactPath(path), $"путь хранилища не точной формы: {path}");
        return (path, bytes);
    }

    /// <summary>Запись справочника системного уровня — едет и в полной, и в настроечной копии.</summary>
    private async Task SeedRecordWithDataAsync(object data)
    {
        var typeId = await SeedCompositeTypeAsync();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DomainObjects.Add(DomainObject.Restore(
            Guid.NewGuid(), typeId, "ООО Ромашка", JsonSerializer.SerializeToDocument(data),
            CatalogScope.System, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null));
        await db.SaveChangesAsync();
    }

    private async Task<byte[]> ExportAsync(BackupScope scope)
    {
        using var s = fixture.Services.CreateScope();
        var (zip, _) = await Backup(s).ExportAsync(scope);
        await using var _zipHandle = zip;
        using var ms = new MemoryStream();
        await zip.CopyToAsync(ms);
        return ms.ToArray();
    }
}
