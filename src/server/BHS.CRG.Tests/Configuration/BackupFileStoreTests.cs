using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Каталог резервных копий (issue #831).
///
/// Проверяется не файловая работа как таковая, а две вещи, которые молча ломаются: <b>имя файла как
/// адрес</b> (всё, что приходит снаружи, обязано остаться внутри каталога) и <b>честность списка</b>
/// — файл без паспорта не прячется, а объясняется, иначе администратор увидит пустой список там,
/// куда только что положил копию.
/// </summary>
public class BackupFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crg-store-{Guid.NewGuid():N}");

    private BackupFileStore Store(int keep = 10)
        => new(BackupStorageOptions.Parse(_dir, keep.ToString(), _dir), NullLogger<BackupFileStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── Имя файла — это адрес ─────────────────────────────────────────────────

    [Theory]
    [InlineData("../secrets.zip")]
    [InlineData("..\\secrets.zip")]
    [InlineData("sub/dir.zip")]
    [InlineData("sub\\dir.zip")]
    [InlineData("/etc/passwd.zip")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_RejectsAnythingButAPlainName(string name)
        => Assert.Throws<InvalidRequestException>(() => Store().Resolve(name));

    /// <summary>Не .zip — тоже отказ: каталог хранит копии, а не произвольные файлы.</summary>
    [Fact]
    public void Resolve_RejectsNonZip()
        => Assert.Throws<InvalidRequestException>(() => Store().Resolve("crg-backup.txt"));

    [Fact]
    public void Resolve_KeepsPlainNameInsideDirectory()
    {
        var path = Store().Resolve("crg-backup-20260822-101500-v0.141.0.zip");
        Assert.Equal(Path.Combine(_dir, "crg-backup-20260822-101500-v0.141.0.zip"), path);
    }

    [Fact]
    public void MissingFile_IsNotFound_NotEmptyStream()
    {
        Directory.CreateDirectory(_dir);
        Assert.Throws<NotFoundException>(() => Store().OpenRead("crg-backup-нет.zip"));
    }

    // ── Предел числа копий ────────────────────────────────────────────────────

    /// <summary>
    /// Каталог заполнен — отказ с текстом, а не молчание и не автоудаление старых: копия, не
    /// снявшаяся тихо, обнаруживается в день аварии, а автоудаление однажды унесёт ту единственную,
    /// которая понадобится (уборка по расписанию — часть 2 эпика, #832).
    /// </summary>
    [Fact]
    public void EnsureRoom_RefusesWhenFull_AndNamesTheWayOut()
    {
        var store = Store(keep: 2);
        WriteBackup("crg-backup-20260101-000000-v0.140.0.zip", records: 1);
        WriteBackup("crg-backup-20260102-000000-v0.140.0.zip", records: 1);

        var ex = Assert.Throws<ConflictException>(store.EnsureRoomForNewCopy);
        Assert.Contains("BACKUP_KEEP_COUNT", ex.Message);
        Assert.Contains("Удалите", ex.Message);
    }

    [Fact]
    public void EnsureRoom_PassesWhileThereIsSpace()
    {
        var store = Store(keep: 2);
        WriteBackup("crg-backup-20260101-000000-v0.140.0.zip", records: 1);
        store.EnsureRoomForNewCopy(); // не бросает
    }

    // ── Список: паспорт, его отсутствие и чужой файл ──────────────────────────

    [Fact]
    public void List_ReadsPassportWithoutTouchingManifest()
    {
        WriteBackup("crg-backup-20260822-101500-v0.141.0.zip", records: 7);

        var file = Assert.Single(Store().List());
        Assert.Equal("0.141.0", file.AppVersion);
        Assert.Equal(2, file.SchemaVersion);
        Assert.Equal(3, file.BlobCount);
        Assert.Equal(7, Assert.Single(file.Sections!).Count);
        Assert.Null(file.Problem);
    }

    /// <summary>
    /// Копия старой версии паспорта не несёт. Восстановить её можно — и список обязан это сказать,
    /// а не показать пустую строку без состава, неотличимую от чужого архива.
    /// </summary>
    [Fact]
    public void List_ExplainsBackupWithoutPassport()
    {
        Directory.CreateDirectory(_dir);
        using (var zip = ZipFile.Open(Path.Combine(_dir, "crg-backup-20250101-000000-v0.100.0.zip"),
                   ZipArchiveMode.Create))
            Write(zip, "manifest.json", "{\"SchemaVersion\":2}");

        var file = Assert.Single(Store().List());
        Assert.Contains("Восстановлению это не мешает", file.Problem);
        Assert.Equal("0.100.0", file.AppVersion); // версия достаётся хотя бы из имени
        Assert.Null(file.Sections);
    }

    [Fact]
    public void List_MarksForeignArchive()
    {
        Directory.CreateDirectory(_dir);
        using (var zip = ZipFile.Open(Path.Combine(_dir, "photos.zip"), ZipArchiveMode.Create))
            Write(zip, "readme.txt", "не копия");

        var file = Assert.Single(Store().List());
        Assert.Contains("не похож на резервную копию", file.Problem);
    }

    /// <summary>Недописанный экспорт не должен выглядеть копией: временный файл в список не идёт.</summary>
    [Fact]
    public void List_IgnoresIncompleteWrites()
    {
        var store = Store();
        var temp = store.CreateTempPath();
        File.WriteAllText(temp, "недописано");
        Assert.Empty(store.List());
    }

    /// <summary>
    /// Расширение в другом регистре не должно делать копию невидимой. Приём файла сверяет
    /// расширение без учёта регистра, а перебор каталога на Linux по умолчанию регистр учитывает:
    /// разойдись эти два правила — загруженная `Backup.ZIP` отвечала бы успехом и пропадала из
    /// списка, не считаясь и в предел.
    ///
    /// Оговорка: на Windows файловая система регистр и так не различает, поэтому здесь тест
    /// прошёл бы и без правки — он закрепляет ПРАВИЛО, а сторожит его прогон на Linux (CI).
    /// </summary>
    [Fact]
    public void List_FindsUppercaseExtension()
    {
        WriteBackup("CRG-BACKUP-20260822-101500-V0.141.0.ZIP", records: 1);
        var store = Store();
        Assert.Single(store.List());
        Assert.Equal(1, store.Count());
    }

    /// <summary>
    /// Две копии, начатые в одну секунду, дают одно имя. Падать на этом нельзя: экспорт к тому
    /// моменту отработал минуты, а в колокольчик ушла бы «внутренняя ошибка» вместо копии.
    /// </summary>
    [Fact]
    public void Publish_DoesNotCollideWithSameSecondName()
    {
        var store = Store();
        var name = BackupFileStore.BuildFileName(DateTimeOffset.UtcNow, "0.141.0");
        WriteBackup(name, records: 1);

        var temp = store.CreateTempPath();
        File.WriteAllBytes(temp, File.ReadAllBytes(Path.Combine(_dir, name)));
        var info = store.Publish(temp, name);

        Assert.NotEqual(name, info.FileName);
        Assert.Equal(2, store.Count());
    }

    /// <summary>
    /// Копия в формате до объединения объектов (schema v1) восстановлению не поддаётся — и список
    /// обязан сказать это, а не пообещать обратное: обещание, которого не держит соседний экран,
    /// хуже молчания.
    /// </summary>
    [Fact]
    public void List_DoesNotPromiseRestoreForObsoleteFormat()
    {
        Directory.CreateDirectory(_dir);
        using (var zip = ZipFile.Open(Path.Combine(_dir, "crg-backup-20250101-000000-v0.60.0.zip"),
                   ZipArchiveMode.Create))
            Write(zip, "manifest.json", "{\"SchemaVersion\":1,\"AppVersion\":\"0.60.0\"}");

        var file = Assert.Single(Store().List());
        Assert.Contains("устаревшем формате (v1)", file.Problem);
        Assert.DoesNotContain("Восстановлению это не мешает", file.Problem);
    }

    // ── Принесённая копия ─────────────────────────────────────────────────────

    /// <summary>
    /// Копия с другого сервера не затирает лежащую: имена там строятся так же, и совпадение
    /// вероятно. Затирание уничтожило бы копию, ради которой файл и принесли.
    /// </summary>
    [Fact]
    public async Task Upload_DoesNotOverwriteExistingName()
    {
        var store = Store();
        WriteBackup("crg-backup-20260822-101500-v0.141.0.zip", records: 1);

        var info = await store.AcceptUploadAsync(
            new MemoryStream(File.ReadAllBytes(Path.Combine(_dir, "crg-backup-20260822-101500-v0.141.0.zip"))),
            "crg-backup-20260822-101500-v0.141.0.zip", CancellationToken.None);

        Assert.Equal("crg-backup-20260822-101500-v0.141.0-2.zip", info.FileName);
        Assert.Equal(2, store.Count());
    }

    /// <summary>Имя из формы — данные, а не путь: каталоги из него не адресуются.</summary>
    [Fact]
    public async Task Upload_SanitizesSuppliedName()
    {
        var store = Store();
        var info = await store.AcceptUploadAsync(new MemoryStream([1, 2, 3]), "../../evil.zip", CancellationToken.None);

        Assert.Equal("evil.zip", info.FileName);
        Assert.True(File.Exists(Path.Combine(_dir, "evil.zip")));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Архив с паспортом — такой же, какой пишет экспорт.</summary>
    private void WriteBackup(string name, int records)
    {
        Directory.CreateDirectory(_dir);
        var summary = new BackupSummary(2, VersionFrom(name), DateTimeOffset.UtcNow, 3,
            [new BackupSectionCount("Типы документов", records)]);

        using var zip = ZipFile.Open(Path.Combine(_dir, name), ZipArchiveMode.Create);
        Write(zip, BackupFileStore.SummaryEntryName,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Write(zip, "manifest.json", "{}");
    }

    private static string VersionFrom(string fileName)
        => Path.GetFileNameWithoutExtension(fileName)[(fileName.LastIndexOf("-v", StringComparison.Ordinal) + 2)..];

    private static void Write(ZipArchive zip, string entryName, string content)
    {
        using var s = zip.CreateEntry(entryName).Open();
        s.Write(Encoding.UTF8.GetBytes(content));
    }
}
