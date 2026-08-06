using BHS.CRG.Api.Configuration;
using BHS.CRG.Api.Endpoints.Common;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел размера резервной копии как настройка развёртывания (issue #711).
///
/// Проверяется не арифметика, а поведение на негодном значении: незаданное берёт умолчание, а
/// заданное с ошибкой — останавливает запуск. Промежуточного «понял как смог» здесь быть не должно,
/// иначе описка в deploy/.env тихо вернула бы 500 МБ на установке, где их специально подняли.
/// </summary>
public class BackupSizeLimitsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotConfigured_FallsBackToDefault(string? value)
        => Assert.Equal(BackupSizeLimits.DefaultMb, BackupSizeLimits.Parse(value).ArchiveMb);

    [Fact]
    public void Configured_IsUsed()
    {
        var limits = BackupSizeLimits.Parse(" 1200 ");
        Assert.Equal(1200, limits.ArchiveMb);
        Assert.Equal(1200L * 1024 * 1024, limits.ArchiveBytes);
    }

    /// <summary>Тело запроса больше архива ровно на запас под обвязку multipart.</summary>
    [Fact]
    public void RequestLimit_LeavesRoomForMultipartOverhead()
    {
        var limits = BackupSizeLimits.Parse("500");
        Assert.Equal(limits.ArchiveBytes + UploadLimits.MultipartOverhead, limits.RequestBytes);
    }

    [Theory]
    [InlineData("пятьсот")]
    [InlineData("500m")]
    [InlineData("500 МБ")]
    [InlineData("1.5")]
    [InlineData("-500")]
    public void Unparsable_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => BackupSizeLimits.Parse(value));

    /// <summary>
    /// Слишком мало — копия пустой системы уже не пройдёт собственное восстановление; слишком
    /// много — согласие выписать на диск столько, сколько прислали, из-за лишнего нуля в .env.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("50000")]
    public void OutOfRange_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => BackupSizeLimits.Parse(value));

    [Fact]
    public void ReadsTheDocumentedConfigurationKey()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:MaxArchiveMb"] = "777" })
            .Build();

        Assert.Equal(777, BackupSizeLimits.FromConfiguration(cfg).ArchiveMb);
    }

    /// <summary>Отказ читает тот, кто разворачивает, — в логе контейнера, где спросить некого.</summary>
    [Fact]
    public void Message_NamesTheVariableAndTheWebServerConsequence()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BackupSizeLimits.Parse("много"));
        Assert.Contains("Backup__MaxArchiveMb", ex.Message);
        Assert.Contains("BACKUP_MAX_ARCHIVE_MB", ex.Message);
        Assert.Contains("client_max_body_size", ex.Message);
    }
}
