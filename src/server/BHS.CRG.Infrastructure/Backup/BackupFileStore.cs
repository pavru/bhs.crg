using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Common;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Каталог резервных копий на диске сервера (issue #831): что в нём лежит, как туда попадает и как
/// оттуда читается.
///
/// <para><b>Почему файлы, а не хранилище блобов.</b> MinIO — часть системы, которую копия и
/// восстанавливает. Копия, лежащая внутри восстанавливаемого, бесполезна ровно в том случае, ради
/// которого делается: хранилище потеряно. Каталог же монтируется с хоста, переживает пересоздание
/// контейнеров, увозится <c>rsync</c>-ом и подкладывается с другого сервера — переезд именно так и
/// делается.</para>
///
/// <para><b>Имя файла — это адрес.</b> Всё, что приходит снаружи (из списка, из формы), проходит
/// через <see cref="Resolve" />: имя обязано быть именем, а не путём. Проверяется не только «нет
/// <c>..</c>», а совпадение с <see cref="Path.GetFileName(string)" /> плюс итоговый полный путь
/// внутри каталога — двух проверок здесь ровно потому, что одна закрывает одну дверь из
/// нескольких.</para>
/// </summary>
public sealed class BackupFileStore(BackupStorageOptions options, ILogger<BackupFileStore> logger)
{
    /// <summary>Паспорт копии внутри архива — см. <see cref="BackupSummary" />.</summary>
    public const string SummaryEntryName = "summary.json";

    private const string ManifestEntryName = "manifest.json";

    /// <summary>Незавершённая запись. Точка в начале и расширение не .zip — в список не попадёт.</summary>
    private const string IncomingPrefix = ".incoming-";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory => options.Directory;

    public int KeepCount => options.KeepCount;

    /// <summary>Имя очередной копии: дата-время и версия системы, которой она снята.</summary>
    public static string BuildFileName(DateTimeOffset at, string appVersion) =>
        $"crg-backup-{at:yyyyMMdd-HHmmss}-v{appVersion}.zip";

    /// <summary>
    /// Что лежит в каталоге, новые сверху. Читается оглавление каждого архива плюс маленький
    /// паспорт — оглавление zip лежит в конце файла и достаётся одним чтением, поэтому размер
    /// архива на стоимость списка не влияет.
    /// </summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        EnsureDirectory();
        return new DirectoryInfo(options.Directory)
            .EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
            .Select(Describe)
            .OrderByDescending(f => f.CreatedAt)
            .ToList();
    }

    /// <summary>Сколько копий в каталоге — с этим числом сверяется предел перед созданием новой.</summary>
    public int Count()
    {
        EnsureDirectory();
        return System.IO.Directory.EnumerateFiles(options.Directory, "*.zip", SearchOption.TopDirectoryOnly).Count();
    }

    /// <summary>
    /// Свободно ли место под ещё одну копию. Отказ — с текстом для человека: молчаливое «копия не
    /// снялась» хуже отказа, потому что обнаруживается в день аварии.
    /// </summary>
    public void EnsureRoomForNewCopy()
    {
        var count = Count();
        if (count < options.KeepCount) return;

        throw new ConflictException(
            $"В каталоге резервных копий уже {count} — это предел ({options.KeepCount}). " +
            "Новая копия не создана: старые не удаляются автоматически, чтобы не потерять ту " +
            "единственную, которая понадобится. Удалите ненужные копии в этом же списке — или " +
            "поднимите предел (BACKUP_KEEP_COUNT в deploy/.env), если места на диске хватает.");
    }

    /// <summary>Открыть копию на чтение — для скачивания и для восстановления.</summary>
    public Stream OpenRead(string fileName)
    {
        var path = Resolve(fileName);
        if (!File.Exists(path)) throw new NotFoundException($"Резервная копия «{fileName}» не найдена.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public long GetSize(string fileName)
    {
        var path = Resolve(fileName);
        if (!File.Exists(path)) throw new NotFoundException($"Резервная копия «{fileName}» не найдена.");
        return new FileInfo(path).Length;
    }

    public void Delete(string fileName)
    {
        var path = Resolve(fileName);
        if (!File.Exists(path)) throw new NotFoundException($"Резервная копия «{fileName}» не найдена.");
        File.Delete(path);
        logger.LogInformation("Резервная копия удалена: {FileName}", fileName);
    }

    /// <summary>
    /// Путь, по которому пишется ещё не готовая копия. Готовая появляется в каталоге одним
    /// движением (<see cref="Publish" />) — иначе прерванный экспорт оставлял бы в списке огрызок,
    /// неотличимый от копии, и восстановление из него отказало бы посреди работы.
    /// </summary>
    public string CreateTempPath()
    {
        EnsureDirectory();
        return Path.Combine(options.Directory, $"{IncomingPrefix}{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Сделать записанный временный файл копией под окончательным именем.</summary>
    public BackupFileInfo Publish(string tempPath, string fileName)
    {
        var target = Resolve(fileName);
        File.Move(tempPath, target, overwrite: false);
        logger.LogInformation("Резервная копия создана: {FileName}", fileName);
        return Describe(new FileInfo(target));
    }

    /// <summary>
    /// Принять копию, загруженную через браузер: файл кладётся в каталог, дальше он ничем не
    /// отличается от снятого здесь. Предел числа копий сюда НЕ применяется — это путь
    /// восстановления, и отказать в нём из-за занятого каталога значило бы запереть систему в тот
    /// самый момент, ради которого каталог и заведён.
    /// </summary>
    public async Task<BackupFileInfo> AcceptUploadAsync(Stream source, string suggestedName, CancellationToken ct)
    {
        EnsureDirectory();
        var fileName = UniqueName(SanitizeName(suggestedName));
        var temp = CreateTempPath();
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous))
                await source.CopyToAsync(fs, ct);
            return Publish(temp, fileName);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Удалить недописанный временный файл — при отказе экспорта.</summary>
    public void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось убрать временный файл копии {Path}", path); }
    }

    /// <summary>
    /// Имя из запроса → путь в каталоге. Всё, что не является простым именем файла с расширением
    /// .zip, отвергается: путь наружу каталога — не опечатка, а способ прочитать или стереть чужой
    /// файл правами сервера.
    /// </summary>
    public string Resolve(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidRequestException("Имя файла резервной копии не указано.");

        var name = fileName.Trim();
        if (name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or "..")
            throw new InvalidRequestException($"Недопустимое имя файла резервной копии: «{fileName}».");

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidRequestException("Резервная копия — файл .zip.");

        var full = Path.GetFullPath(Path.Combine(options.Directory, name));
        var root = options.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidRequestException($"Недопустимое имя файла резервной копии: «{fileName}».");

        return full;
    }

    /// <summary>
    /// Байты паспорта. Отдельным методом, потому что их считает не только запись архива, но и
    /// оценка размера копии: разойдись эти два места форматом — оценка молча разошлась бы с
    /// архивом, а сходство их проверяется тестом с точностью до сотен байт.
    /// </summary>
    public static byte[] SummaryBytes(BackupSummary summary)
        => JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);

    /// <summary>Записать паспорт копии первой записью архива.</summary>
    public static async Task WriteSummaryAsync(ZipArchive zip, BackupSummary summary, CancellationToken ct)
    {
        var entry = zip.CreateEntry(SummaryEntryName, CompressionLevel.Fastest);
        await using var w = entry.Open();
        await w.WriteAsync(SummaryBytes(summary), ct);
    }

    private void EnsureDirectory() => System.IO.Directory.CreateDirectory(options.Directory);

    private BackupFileInfo Describe(FileInfo file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file.FullName);
            var summaryEntry = zip.GetEntry(SummaryEntryName);
            if (summaryEntry is not null)
            {
                using var s = summaryEntry.Open();
                var summary = JsonSerializer.Deserialize<BackupSummary>(s, JsonOptions);
                if (summary is not null)
                    return new BackupFileInfo(file.Name, file.Length, summary.CreatedAt,
                        summary.AppVersion, summary.SchemaVersion, summary.BlobCount, summary.Sections, null);
            }

            // Паспорта нет. Это либо копия, снятая версией до issue #831, либо чужой zip. Разница
            // существенная — восстанавливать первое можно, второе нет, — и молчать о ней нельзя.
            var problem = zip.GetEntry(ManifestEntryName) is not null
                ? "Копия снята версией до 0.141.0: состав и дату из неё не прочитать. Восстановить можно."
                : "Файл не похож на резервную копию системы: в архиве нет manifest.json.";
            return new BackupFileInfo(file.Name, file.Length, file.LastWriteTimeUtc,
                VersionFromName(file.Name), null, null, null, problem);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось прочитать резервную копию {FileName}", file.Name);
            return new BackupFileInfo(file.Name, file.Length, file.LastWriteTimeUtc, null, null, null, null,
                $"Файл не читается как архив: {ex.Message}");
        }
    }

    /// <summary>Версия из имени вида <c>crg-backup-20260822-141530-v0.140.1.zip</c>; иначе null.</summary>
    private static string? VersionFromName(string fileName)
    {
        var i = fileName.LastIndexOf("-v", StringComparison.Ordinal);
        if (i < 0) return null;
        var v = Path.GetFileNameWithoutExtension(fileName)[(i + 2)..];
        return v.Length > 0 && v.All(c => char.IsDigit(c) || c is '.' or '-') ? v : null;
    }

    private static string SanitizeName(string name)
    {
        var bare = Path.GetFileName(name.Trim());
        foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(bare) || bare is "." or "..")
            bare = $"crg-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        if (!bare.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) bare += ".zip";
        return bare;
    }

    /// <summary>Принесённая копия не затирает лежащую: одноимённых бывает две с разных серверов.</summary>
    private string UniqueName(string name)
    {
        if (!File.Exists(Path.Combine(options.Directory, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem}-{i}.zip";
            if (!File.Exists(Path.Combine(options.Directory, candidate))) return candidate;
        }
        throw new ConflictException($"В каталоге уже слишком много копий с именем «{name}».");
    }
}
