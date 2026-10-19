using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Файлы ядра, которых после восстановления нет нигде, — часть <see cref="BackupServiceTests" />
/// (issue #1437).
///
/// <para>Копия, снятая версией, которая какое-то место хранения пути не обходила, восстанавливалась с
/// «Файлы: восстановлено N из N»: в числе только архив, а о файлах, на которые ссылаются вернувшиеся
/// записи, отчёт молчал. «Старая копия» здесь — настоящая копия без записи файла и без признака
/// <see cref="BackupManifest.KnowsCoreFileScan" />.</para>
/// </summary>
public partial class BackupServiceTests
{
    private const string RecordsLackFiles = "В копии нет файлов, на которые ссылаются записи";

    /// <summary>
    /// Проверка срабатывает: файлов нет ни в архиве, ни в хранилище — отчёт называет число, поле и
    /// выход. Причина — по пути (ревью PR #1440): «версия не обходила» — только о пути, который нашёл
    /// один обход; путь из списка мест брала и прежняя версия, и его не было в хранилище при снятии.
    /// </summary>
    [Fact]
    public async Task OldCopyWithoutFiles_RestoreNamesThem_EachWithItsCause()
    {
        var (listed, _) = await StoreAsync("подпись.png");
        var (scanned, _) = await StoreAsync("печать.png");
        await SeedRecordWithDataAsync(new
        {
            Подпись = new { _type = "file", blobPath = listed },
            Печать = scanned,
        }.Typed());

        var zipBytes = AsOldCopy(WithoutEntries(
            await ExportAsync(BackupScope.Full), [$"blobs/{listed}", $"blobs/{scanned}"]));
        await DropStoredAsync(listed);
        await DropStoredAsync(scanned);
        await fixture.ResetDatabaseAsync();
        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();
        var asked = storage.SizeQueries;
        var report = await ImportZipAsync(zipBytes);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        // Путей нет в реестре — «нет» говорит реестр, одним запросом; хранилище о них не спрашивают
        // (issue #1439). У копии, снятой при недоступном хранилище, таких путей тысячи.
        Assert.Equal(asked, storage.SizeQueries);
        Assert.Contains(report.Warnings, w =>
            w.Contains(RecordsLackFiles) && w.Contains("CommonDataEntries[].Data (2)") &&
            w.Contains("1 из них лежат в полях, которые версия, снявшая копию, не обходила") &&
            w.Contains("снимите копию заново") && w.Contains("Остальных не было в хранилище"));
    }

    /// <summary>
    /// Пропал файл набора — набор назван по имени, а частям не обещан выход «Разбиение»: резать их
    /// не из чего (ревью PR #1440).
    /// </summary>
    [Fact]
    public async Task LostDataSetFile_IsNamed_AndPartsAreNotPromisedRecut()
    {
        var (fileId, _, parts) = await SeedSplitDataSetAsync();
        string filePath;
        using (var scope = fixture.Services.CreateScope())
            filePath = (await scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>()
                .DataSetFiles.FindAsync(fileId))!.BlobPath;

        var drop = parts.Keys.Append(filePath).Select(p => $"blobs/{p}").ToHashSet();
        var zipBytes = WithoutEntries(await ExportFullAsync(), drop);
        await WipeAsync(fileId, parts.Keys);
        var report = await ImportZipAsync(zipBytes);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w => w.StartsWith("Файлов наборов данных нет") && w.Contains("«Альбом ЭМ»"));
        Assert.Contains(report.Warnings, w => w.Contains("вырезать их заново не из чего") && w.Contains("«Альбом ЭМ» — 2 из 2"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Части вырезаются заново"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(RecordsLackFiles));
    }

    /// <summary>
    /// Карточка, которую восстановление пропустило, в базе не появилась — «откроется без скана» о ней
    /// было бы неправдой (ревью PR #1440).
    /// </summary>
    [Fact]
    public async Task SkippedQualityDocument_IsNotCountedWithoutScan()
    {
        var manifest = ManifestWith([],
            [
                new BackupQualityDocument(Guid.NewGuid(), Guid.NewGuid(), "Сертификат неизвестного типа",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    "quality/lost.pdf", "lost.pdf", "application/pdf",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.Contains(report.Warnings, w => w.Contains("не найден, пропущен"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("скан не восстановлен"));
    }

    /// <summary>
    /// Копия с признаком: файла не было в хранилище уже при снятии — причина названа эта, а не «версия
    /// не брала».
    /// </summary>
    [Fact]
    public async Task NewCopy_FileMissingAtSnapshot_NamesThatCause()
    {
        var (path, _) = await StoreAsync("подпись.png");
        await SeedRecordWithDataAsync(new { Подпись = path });
        await DropStoredAsync(path);

        var zipBytes = await ExportAsync(BackupScope.Full);
        await fixture.ResetDatabaseAsync();
        var report = await ImportZipAsync(zipBytes);

        Assert.Contains(report.Warnings, w =>
            w.Contains(RecordsLackFiles) && w.Contains("уже при снятии копии") && !w.Contains("снимите копию заново"));
    }

    /// <summary>
    /// Исправная копия: файлы в архиве — хранилище не спрашивается, отчёт молчит. И файл, которого в
    /// архиве нет, но который уцелел на этой установке, — не потеря.
    /// </summary>
    [Fact]
    public async Task CompleteCopy_OrFileSurvivingHere_IsNotReported()
    {
        var (path, _) = await StoreAsync("подпись.png");
        await SeedRecordWithDataAsync(new { Подпись = path });
        var zipBytes = await ExportAsync(BackupScope.Full);

        await DropStoredAsync(path);
        await fixture.ResetDatabaseAsync();
        var complete = await ImportZipAsync(zipBytes);
        Assert.DoesNotContain(complete.Warnings, w => w.Contains(RecordsLackFiles));

        // Поверх живой системы, где файл уже на месте: архив без него — и всё равно молчание.
        var survived = await ImportZipAsync(AsOldCopy(WithoutEntries(zipBytes, [$"blobs/{path}"])));
        Assert.DoesNotContain(survived.Warnings, w => w.Contains(RecordsLackFiles));
    }

    /// <summary>
    /// Хранилище недоступно во время восстановления — отчёт говорит «проверить не удалось», а не
    /// объявляет файлы потерянными (issue #1427).
    ///
    /// <para>Прежде отказ хранилища выглядел отсутствием файла, и отчёт называл потерянным файл, который
    /// лежит на месте; спасала его только оговорка в конце отчёта «на недоступность оно отвечает так же».
    /// Файл здесь уцелел на этой установке — пропасть ему нечем, недоступна только связь.</para>
    /// </summary>
    [Fact]
    public async Task StorageUnreachable_DuringRestore_SaysCheckFailed_NotLost()
    {
        var (path, _) = await StoreAsync("подпись.png");
        await SeedRecordWithDataAsync(new { Подпись = path });
        var zipBytes = AsOldCopy(WithoutEntries(await ExportAsync(BackupScope.Full), [$"blobs/{path}"]));

        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();
        RestoreReport report;
        try
        {
            storage.Offline = true;
            report = await ImportZipAsync(zipBytes);
        }
        finally { storage.Offline = false; }

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(RecordsLackFiles));
        Assert.Contains(report.Warnings, w =>
            w.Contains("проверить, на месте ли файлы, на которые они ссылаются, не удалось") &&
            w.Contains("хранилище файлов отказало в ответе"));
    }

    /// <summary>
    /// Слова — по хозяину файла: у ассета, сформированного PDF и скана свой выход, а в общий счёт
    /// записей они не попадают.
    /// </summary>
    [Fact]
    public async Task MissingFiles_AreNamedByTheirPlace()
    {
        var now = DateTimeOffset.UtcNow;
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var manifest = ManifestWith([]) with
        {
            TemplateAssets =
            [
                new BackupTemplateAsset(Guid.NewGuid(), nameof(CatalogScope.System), null, "Font",
                    "Шрифт", "font.ttf", "font/ttf", "assets/lost-font.ttf", "Lost", now, now),
            ],
            Documents =
            [
                new BackupDocument(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "АОСР 1", empty, [], "Draft", 0,
                    null, null, null, empty,
                    [new BackupGeneratedFile(Guid.NewGuid(), "Pdf", "generated/lost.pdf", null, now, now)], now, now),
            ],
        };

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Contains(report.Warnings, w => w.StartsWith("Ассеты шаблонов: 1") && w.Contains("загрузите ассеты"));
        Assert.Contains(report.Warnings, w => w.StartsWith("Сформированные PDF документов: 1") && w.Contains("сформируйте"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains(RecordsLackFiles));
    }

    /// <summary>
    /// Скан, которого в архиве нет, но который уцелел на этой установке, потерянным не объявляется.
    /// Прежде предупреждение смотрело только в архив и звучало и тогда.
    /// </summary>
    [Fact]
    public async Task QualityScan_SurvivingHere_IsNotReported()
    {
        var (scan, _) = await StoreAsync("сертификат.pdf");
        var docTypeId = Guid.NewGuid();
        var manifest = ManifestWith(
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            [
                new BackupQualityDocument(Guid.NewGuid(), docTypeId, "Сертификат со сканом",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    scan, "сертификат.pdf", "application/pdf",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        // Без очистки: очистка базы снимает и реестр файлов, а незарегистрированный файл не наш.
        var report = await ImportManifestZipAsync(manifest);

        Assert.Equal(1, report.QualityDocumentsCreated);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("скан не восстановлен"));
    }

    /// <summary>
    /// Паспорт копии называет пропавшие части PDF-наборов по набору и с тем же выходом, что у
    /// восстановления: «Разбиение» → «Сохранить». Прежде он говорил одно «файлов не оказалось: N из M»
    /// — и на продовой копии 50 таких файлов оставались безымянными, хотя все 50 вырезаются заново.
    /// Оценка веса пишет те же слова: её паспорт обязан весить столько же (#1425).
    /// </summary>
    [Fact]
    public async Task Copy_MissingParts_NamesSets_AndRecut()
    {
        var (_, _, parts) = await SeedSplitDataSetAsync();
        foreach (var part in parts.Keys) await DropStoredAsync(part);

        BackupSizeEstimate estimate;
        using (var scope = fixture.Services.CreateScope())
            estimate = await Backup(scope).EstimateSizeAsync(long.MaxValue, BackupScope.Full);
        var zipBytes = await ExportFullAsync();
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var warnings = SummaryOf(zip).Warnings ?? [];

        Assert.Contains(warnings, w =>
            w.StartsWith("Вырезанных PDF документов не оказалось в хранилище: «Альбом ЭМ» — 2 из 2.") &&
            w.Contains("откройте у набора «Разбиение» и сохраните его") && w.Contains("снимите копию заново"));
        Assert.DoesNotContain(warnings, w => w.Contains("вырезать их заново не из чего"));
        // Других потерь нет — и общей фразы нет: она сосчитала бы те же части второй раз.
        Assert.DoesNotContain(warnings, w => w.Contains("Файлов не оказалось") || w.Contains("Других файлов"));
        Assert.Equal(2, estimate.Variant.MissingBlobCount);
        AssertEstimateMatchesArchive(estimate.Variant.TotalBytes, zipBytes.LongLength);
    }

    /// <summary>
    /// Нет и файла набора — «Разбиение» частей не вернёт: выход — сначала вернуть файл. Сам файл набора
    /// назван общей фразой, «других», и в счёт частей не попадает.
    /// </summary>
    [Fact]
    public async Task Copy_MissingPartsAndSetFile_SaysReturnTheFileFirst()
    {
        var (fileId, _, parts) = await SeedSplitDataSetAsync();
        string filePath;
        using (var scope = fixture.Services.CreateScope())
            filePath = (await scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>()
                .DataSetFiles.FindAsync(fileId))!.BlobPath;
        foreach (var path in parts.Keys.Append(filePath)) await DropStoredAsync(path);

        using var zip = new ZipArchive(new MemoryStream(await ExportFullAsync()), ZipArchiveMode.Read);
        var warnings = SummaryOf(zip).Warnings ?? [];

        Assert.Contains(warnings, w =>
            w.Contains("вырезать их заново не из чего") && w.Contains("«Альбом ЭМ» — 2 из 2") &&
            w.Contains("Верните файл набора (загрузите заново)"));
        Assert.DoesNotContain(warnings, w => w.Contains("Части вырезаются заново"));
        Assert.Contains(warnings, w => w.StartsWith("Других файлов не оказалось в хранилище: 1 из 3"));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private async Task DropStoredAsync(string path)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(path);
    }

    /// <summary>Тот же архив, но манифест — как у версии без обхода: признака нет.</summary>
    private static byte[] AsOldCopy(byte[] zipBytes)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName != "manifest.json")
                {
                    from.CopyTo(to);
                    continue;
                }
                var manifest = JsonNode.Parse(from)!.AsObject();
                Assert.True(manifest.Remove(nameof(BackupManifest.KnowsCoreFileScan)), "в манифесте нет признака");
                using var writer = new Utf8JsonWriter(to);
                manifest.WriteTo(writer);
            }
        }
        return output.ToArray();
    }
}

file static class FileReference
{
    /// <summary><c>_type</c> → <c>$type</c>: у анонимного типа имя свойства с «$» не написать.</summary>
    public static JsonNode Typed(this object value)
    {
        var json = JsonSerializer.Serialize(value).Replace("\"_type\"", "\"$type\"");
        return JsonNode.Parse(json)!;
    }
}
