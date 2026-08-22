using BHS.CRG.Application.Backup;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Настройки каталога копий как настройки развёртывания (issue #831).
///
/// Проверяется то же правило, что и у предела размера (#711): незаданное берёт умолчание, заданное
/// с ошибкой — останавливает запуск. Промежуточного «понял как смог» быть не должно: описка в
/// deploy/.env не имеет права тихо превратиться в другое число копий.
/// </summary>
public class BackupStorageOptionsTests
{
    private static readonly string Fallback = Path.Combine(Path.GetTempPath(), "crg-fallback");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KeepCount_NotConfigured_FallsBackToDefault(string? value)
        => Assert.Equal(BackupStorageOptions.DefaultKeepCount,
            BackupStorageOptions.Parse(null, value, Fallback).KeepCount);

    [Fact]
    public void KeepCount_Configured_IsUsed()
        => Assert.Equal(25, BackupStorageOptions.Parse(null, " 25 ", Fallback).KeepCount);

    [Theory]
    [InlineData("десять")]
    [InlineData("10.5")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("100000")]
    public void KeepCount_Invalid_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => BackupStorageOptions.Parse(null, value, Fallback));

    /// <summary>Путь приводится к абсолютному: относительный зависел бы от рабочего каталога.</summary>
    [Fact]
    public void Directory_IsMadeAbsolute()
    {
        var opts = BackupStorageOptions.Parse("backups", null, Fallback);
        Assert.True(Path.IsPathRooted(opts.Directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Directory_NotConfigured_UsesFallback(string? value)
        => Assert.Equal(Path.GetFullPath(Fallback), BackupStorageOptions.Parse(value, null, Fallback).Directory);

    /// <summary>
    /// Каталог, в который нельзя писать, обязан останавливать запуск с текстом про владельца: это
    /// самый вероятный отказ подсистемы (каталог с хоста принадлежит root, контейнер — нет), и
    /// узнать о нём при попытке снять копию значит узнать в тот единственный раз, когда копия
    /// была нужна.
    /// </summary>
    [Fact]
    public void EnsureUsable_UnwritablePath_RefusesWithOwnerHint()
    {
        // Файл вместо каталога: CreateDirectory на существующем файле отказывает на любой ОС.
        var file = Path.Combine(Path.GetTempPath(), $"crg-not-a-dir-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => BackupStorageOptions.Parse(file, null, Fallback).EnsureUsable());
            Assert.Contains("app", ex.Message);
            Assert.Contains("BACKUP_DIR", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void EnsureUsable_CreatesMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crg-new-{Guid.NewGuid():N}");
        try
        {
            BackupStorageOptions.Parse(dir, null, Fallback).EnsureUsable();
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
