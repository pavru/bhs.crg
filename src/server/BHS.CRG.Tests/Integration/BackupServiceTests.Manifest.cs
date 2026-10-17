using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Паспорт копии и оценка её веса — часть <see cref="BackupServiceTests" />.
///
/// <para>Сюда собрано то, что говорит о копии, ещё её не разворачивая: версия приложения в
/// манифесте, предварительная оценка размера (<c>EstimateSizeAsync</c> строит манифест и мерит
/// его) и запись пропавших блобов в паспорт при выгрузке.</para>
/// </summary>
public partial class BackupServiceTests
{
    /// <summary>
    /// Версия в манифесте — то, ради чего поле существует: «какой сборкой снята копия». Константа в
    /// коде делала все копии одинаковыми независимо от сборки.
    /// </summary>
    [Fact]
    public void Manifest_AppVersion_MatchesAssemblyVersion()
    {
        // Сверяем с ДРУГОЙ сборкой решения: версия у всех проектов общая (Directory.Build.props),
        // поэтому совпадение здесь означает «взято из сборки», а не «сравнили значение с собой».
        var solutionVersion = typeof(BackupManifest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];

        // Проверка «не 1.0.0» тут была бы миной: 1.0.0 — это заявленный первый релиз, и в день его
        // выпуска тест покраснел бы на ровном месте. Совпадение с версией сборки и так доказывает,
        // что значение берётся из сборки, а не написано в коде.
        Assert.Equal(solutionVersion, BackupService.CurrentAppVersion);
    }

    /// <summary>
    /// Оценка веса копии сходится с настоящим архивом (issue #711).
    ///
    /// Это главная проверка новой оценки, и сверяется она не с ожидаемым числом, а с ФАКТОМ:
    /// снимаем копию тех же данных и сравниваем длины. Число, посчитанное по своей же формуле,
    /// доказывало бы только то, что формула не менялась, — а сходиться она обязана с zip.
    ///
    /// Скан берём заведомо несжимаемый (псевдослучайные байты): именно так ведут себя сканы в
    /// PDF, и именно поэтому они кладутся в архив без сжатия. Данные, которые сжимаются в ноль,
    /// скрыли бы ошибку в учёте того, что сжимается, а что нет.
    /// </summary>
    [Fact]
    public async Task EstimateSize_MatchesActualArchive()
    {
        const string scanPath = "quality/2026/big-certificate.pdf";
        var scanBytes = new byte[256 * 1024];
        new Random(711).NextBytes(scanBytes);
        var docTypeId = Guid.NewGuid();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

            await blob.PutAsync(AssetBlobPath, new MemoryStream(AssetBytes), "image/png", default);
            db.TemplateAssets.Add(TemplateAsset.Restore(Guid.NewGuid(), TemplateAssetScope.System, null,
                TemplateAssetKind.Image, "logo", "logo.png", "image/png", AssetBlobPath, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            await blob.PutAsync(scanPath, new MemoryStream(scanBytes), "application/pdf", default);
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "ЕАЭС RU С-RU.АТ21.В.00157", JsonDocument.Parse("""{"Номер":"00157"}"""),
                CatalogScope.System, null, QualityDocSource.Web, null,
                scanPath, "big-certificate.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        BackupSizeEstimate estimate;
        long actualBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Backup(scope);
            estimate = await svc.EstimateSizeAsync(limitBytes: 500L * 1024 * 1024);

            var (zipStream, _) = await svc.ExportAsync();
            await using var _zipHandle = zipStream;
            actualBytes = zipStream.Length;
        }

        // Сверяем КОНФИГУРАЦИОННЫЙ состав: именно его и снял ExportAsync выше (issue #833).
        var config = estimate.Variant;
        Assert.Equal(2, config.BlobCount);
        Assert.Equal(0, config.MissingBlobCount);
        // Блобы лежат в архиве как есть — их вклад точен, а не приближён.
        Assert.Equal(scanBytes.LongLength + AssetBytes.LongLength, config.BlobBytes);
        Assert.False(config.TotalBytes > estimate.LimitBytes);

        // Расхождение — считаные байты: заголовки zip считаются по длине имени, а не круглой
        // константой (круглая занижала оценку на рабочей базе почти на 6 КБ).
        AssertEstimateMatchesArchive(config.TotalBytes, actualBytes);
    }

    /// <summary>
    /// Файл, которого нет в хранилище, попадает в ПАСПОРТ копии — а не только в журнал сервера.
    ///
    /// Поле «что пропущено» заведено затем, чтобы узнать о пропаже при снятии копии, а не при
    /// восстановлении, то есть не после аварии. До issue #833 паспорт писался ДО прогона по
    /// блобам и рассказать об этом не мог по устройству.
    /// </summary>
    [Fact]
    public async Task Export_RecordsMissingBlobsInPassport()
    {
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "Сертификат без скана", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null,
                "quality/2026/потерян.pdf", "потерян.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync();
            await using var _zipHandle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        using var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        await using var entry = check.GetEntry("summary.json")!.Open();
        // Разбираем JSON, а не ищем подстроку: кириллица в паспорте экранируется (\uXXXX), и
        // поиск по тексту не нашёл бы даже то, что там есть.
        var passport = await JsonSerializer.DeserializeAsync<BackupSummary>(
            entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(passport!.Warnings!, w => w.Contains("не оказалось в хранилище"));
    }

    /// <summary>
    /// Файл, оборвавшийся ПОСРЕДИ переноса в архив, — отказ экспорта, а не «пропущен».
    ///
    /// Запись в архиве к этому времени уже заведена и наполовину записана; посчитай экспорт такой
    /// файл пропавшим и иди дальше — копия вышла бы с обрезанным файлом, который восстановление
    /// положит на место целого, а паспорт скажет «в копию не попал». Пропуском прощается только
    /// отказ открыть файл (issue #1425): на этом же держится оценка веса.
    /// </summary>
    [Fact]
    public async Task Export_FailsWhenBlobBreaksMidCopy()
    {
        const string scanPath = "quality/2026/оборвётся.pdf";
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
                .PutAsync(scanPath, new MemoryStream(new byte[64 * 1024]), "application/pdf", default);
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "Сертификат", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null,
                scanPath, "оборвётся.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var fake = fixture.Services.GetRequiredService<FakeBlobStorage>();
        fake.BreakBodyOf = scanPath;
        try
        {
            using var scope = fixture.Services.CreateScope();
            await Assert.ThrowsAsync<IOException>(() => Backup(scope).ExportAsync());
        }
        finally
        {
            fake.BreakBodyOf = null;
        }
    }

    /// <summary>
    /// Оценка не завышает на битых ссылках. Экспорт недоступный блоб пропускает с предупреждением и
    /// записи в архиве не заводит — значит и веса он не добавляет, ни байтами, ни заголовком; но
    /// молчать о нём тоже нельзя: битая ссылка иначе не всплывёт нигде, кроме лога экспорта.
    ///
    /// Сверяем с настоящим архивом, а не только счётчики: заголовок записи прибавлялся и пропавшему
    /// файлу (issue #1425), и счётчики этого не видели — оценка расходилась с архивом на 3,6 КБ при
    /// десяти пропавших. Пропавших берём несколько и с длинными именами: заголовок одного весит
    /// ~160 байт и в допуск 256 прошёл бы. Рядом — один файл, который в хранилище ЕСТЬ, с таким же
    /// длинным именем: заголовок положен ему, и правка, отнявшая заголовок у всех, тоже краснеет.
    /// </summary>
    [Fact]
    public async Task EstimateSize_CountsMissingBlobsSeparately_AndDoesNotChargeForThem()
    {
        var missingPaths = Enumerable.Range(1, 4)
            .Select(i => $"quality/2026/сертификат-которого-нет-в-хранилище-{i}.pdf").ToList();
        const string presentPath = "quality/2026/сертификат-который-в-хранилище-есть-и-ляжет-в-копию.pdf";
        byte[] presentBytes = [7, 7, 7, 7];
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
                .PutAsync(presentPath, new MemoryStream(presentBytes), "application/pdf", default);
            foreach (var path in missingPaths.Append(presentPath))
                db.QualityDocuments.Add(QualityDocument.Restore(
                    Guid.NewGuid(), docTypeId, "Сертификат", JsonDocument.Parse("{}"),
                    CatalogScope.System, null, QualityDocSource.Manual, null,
                    path, "файл.pdf", "application/pdf",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        BackupSizeEstimate estimate;
        long actualBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Backup(scope);
            estimate = await svc.EstimateSizeAsync(limitBytes: 500L * 1024 * 1024);

            var (zipStream, _) = await svc.ExportAsync();
            await using var _zipHandle = zipStream;
            actualBytes = zipStream.Length;
        }

        Assert.Equal(missingPaths.Count + 1, estimate.Variant.BlobCount);
        Assert.Equal(missingPaths.Count, estimate.Variant.MissingBlobCount);
        Assert.Equal(presentBytes.LongLength, estimate.Variant.BlobBytes);
        AssertEstimateMatchesArchive(estimate.Variant.TotalBytes, actualBytes);
    }

    /// <summary>
    /// Хранилище не отвечает — оценка отвечает отказом, а не копией, в которой пропало всё
    /// (issue #1427).
    ///
    /// <para>Прежде отказ хранилища выглядел отсутствием файла, и экран настроек показывал крошечный
    /// архив «с пропавшими файлами»: веса нет, а предупреждение говорит о битых ссылках, которых
    /// нет. Файл здесь лежит на месте — пропасть ему нечем, недоступна только связь.</para>
    /// </summary>
    [Fact]
    public async Task EstimateSize_StorageUnreachable_RefusesInsteadOfReportingMissingFiles()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
                .PutAsync(AssetBlobPath, new MemoryStream(AssetBytes), "image/png", default);
            db.TemplateAssets.Add(TemplateAsset.Restore(Guid.NewGuid(), TemplateAssetScope.System, null,
                TemplateAssetKind.Image, "logo", "logo.png", "image/png", AssetBlobPath, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var storage = fixture.Services.GetRequiredService<FakeBlobStorage>();
        Exception? failure;
        try
        {
            storage.Offline = true;
            using var scope = fixture.Services.CreateScope();
            failure = await Record.ExceptionAsync(() => Backup(scope).EstimateSizeAsync(limitBytes: 500L * 1024 * 1024));
        }
        finally { storage.Offline = false; }

        // Отказ — наш, с текстом для человека: экран показывает его вместо «оценить не удалось».
        var refusal = Assert.IsType<BackupSizeUnknownException>(failure);
        Assert.Contains("хранилище", refusal.Message);
    }

    /// <summary>
    /// Предел — не украшение: копия сверх него помечена как непринимаемая. Проверяем негативом,
    /// подставив предел ниже фактического веса, — иначе признак остался бы вычислением, которое
    /// никогда не срабатывало.
    /// </summary>
    [Fact]
    public async Task EstimateSize_MarksCopyThatWouldBeRejected()
    {
        using var scope = fixture.Services.CreateScope();
        var estimate = await Backup(scope).EstimateSizeAsync(limitBytes: 1);

        Assert.True(estimate.Variant.TotalBytes > 1);
        Assert.True(estimate.Variant.TotalBytes > estimate.LimitBytes);
    }
}
