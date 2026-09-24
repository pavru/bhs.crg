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
        var diff = Math.Abs(config.TotalBytes - actualBytes);
        Assert.True(diff < 256,
            $"оценка {config.TotalBytes} против архива {actualBytes} (разница {diff} байт)");
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
    /// Оценка не завышает на битых ссылках. Экспорт недоступный блоб пропускает с предупреждением —
    /// значит и веса он не добавляет; но молчать о нём тоже нельзя: битая ссылка иначе не всплывёт
    /// нигде, кроме лога экспорта.
    /// </summary>
    [Fact]
    public async Task EstimateSize_CountsMissingBlobsSeparately_AndDoesNotChargeForThem()
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
                Guid.NewGuid(), docTypeId, "Сертификат без файла", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null,
                "quality/2026/pointer-to-nowhere.pdf", "нет.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var estimateScope = fixture.Services.CreateScope();
        var estimate = await Backup(estimateScope).EstimateSizeAsync(limitBytes: 500L * 1024 * 1024);

        Assert.Equal(1, estimate.Variant.BlobCount);
        Assert.Equal(1, estimate.Variant.MissingBlobCount);
        Assert.Equal(0, estimate.Variant.BlobBytes);
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
