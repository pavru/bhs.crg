using System.Globalization;

namespace BHS.CRG.Application.Backup;

/// <summary>
/// Каталог, в котором система держит резервные копии, и сколько их разрешено держать (issue #831).
///
/// <para><b>Почему копия перестала быть только загрузкой в браузер.</b> Экспорт собирался во
/// временный файл и сразу уходил в ответ, на сервере не оставаясь; восстановление было загрузкой
/// multipart-ом. Весь <c>BACKUP_MAX_ARCHIVE_MB</c> существует ради этого пути — nginx → Kestrel →
/// разбор формы → проверка (#482, #711). С ростом библиотеки качества копия создаётся, а
/// восстановление отказывает: предел упирается в потолок 10 ГБ, и браузерная передача гигабайтов
/// ненадёжна сама по себе. Предел — свойство транспорта, поэтому меняем транспорт: копия ложится в
/// каталог на хосте, а восстановление читает её оттуда, не пересекая сеть.</para>
///
/// <para><b>Bind-mount, а не именованный том.</b> Каталог обязан быть доступен администратору по
/// пути: копию увозят <c>rsync</c>-ом и подкладывают с другого сервера — ровно этим переезд и
/// делается. Внутрь тома Docker так не заглянешь.</para>
/// </summary>
public sealed class BackupStorageOptions
{
    /// <summary>
    /// Сколько копий разрешено держать в каталоге по умолчанию. Не «сколько влезет»: диск кончается
    /// молча, а копия, не снявшаяся из-за нехватки места, обнаруживается в день аварии.
    /// </summary>
    public const int DefaultKeepCount = 10;

    public const int MinKeepCount = 1;

    /// <summary>Верхняя граница — против описки в <c>.env</c>, как у предела размера архива.</summary>
    public const int MaxKeepCount = 1000;

    private const string DirKey = "Backup:Directory";
    private const string KeepKey = "Backup:KeepCount";
    private const string DirEnv = "Backup__Directory";
    private const string KeepEnv = "Backup__KeepCount";
    private const string DirCompose = "BACKUP_DIR";
    private const string KeepCompose = "BACKUP_KEEP_COUNT";

    private BackupStorageOptions(string directory, int keepCount)
    {
        Directory = directory;
        KeepCount = keepCount;
    }

    /// <summary>Абсолютный путь к каталогу копий (внутри контейнера — точка монтирования).</summary>
    public string Directory { get; }

    /// <summary>Предельное число копий в каталоге. Достигнут — новая не создаётся (см. remarks).</summary>
    /// <remarks>
    /// Ограничение действует на СОЗДАНИЕ копий, а не на приём принесённых. Загрузка файла с другого
    /// сервера — путь восстановления, и отказать в нём потому, что каталог занят старыми копиями,
    /// значило бы запереть систему ровно в тот момент, ради которого всё это заведено. Уборка
    /// старых копий по расписанию — часть 2 эпика (#832); здесь переполнение — отказ с объяснением.
    /// </remarks>
    public int KeepCount { get; }

    /// <summary>
    /// Разбирает настройки и приводит путь к абсолютному. Пустой путь — не отказ, а
    /// <paramref name="fallbackDirectory" /> (каталог рядом с приложением): установке из исходников
    /// настраивать тут нечего. Негодное же значение — именно отказ, в тон прочим проверкам
    /// конфигурации: «10» и «десять» не должны различаться тем, что второе молча даёт умолчание.
    /// </summary>
    public static BackupStorageOptions Parse(string? directory, string? keepCount, string fallbackDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? fallbackDirectory : directory.Trim();
        dir = Path.GetFullPath(dir);

        var keep = DefaultKeepCount;
        if (!string.IsNullOrWhiteSpace(keepCount))
        {
            if (!int.TryParse(keepCount.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out keep))
                throw Fail($"Предельное число резервных копий ({KeepKey}) не число: «{keepCount.Trim()}».",
                    KeepEnv, KeepCompose);

            if (keep < MinKeepCount || keep > MaxKeepCount)
                throw Fail(
                    $"Предельное число резервных копий ({KeepKey}) вне допустимого диапазона: {keep}. " +
                    $"Допустимо от {MinKeepCount} до {MaxKeepCount}.",
                    KeepEnv, KeepCompose);
        }

        return new BackupStorageOptions(dir, keep);
    }

    /// <summary>
    /// Создаёт каталог и убеждается, что в него можно писать. Проверка на СТАРТЕ, а не при первой
    /// копии: контейнер работает не от root, и каталог, созданный на хосте с чужим владельцем, —
    /// самый вероятный отказ этой подсистемы. Узнать о нём при попытке снять копию значит узнать
    /// в тот единственный раз, когда копия была нужна.
    /// </summary>
    public void EnsureUsable()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var probe = Path.Combine(Directory, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Каталог резервных копий «{Directory}» недоступен для записи ({ex.Message}). " +
                "Приложение не запущено намеренно: без него система не сможет снять ни одной копии, " +
                "а выяснилось бы это при первой попытке. В поставке Docker каталог задаётся " +
                $"переменной {DirCompose} в deploy/.env и монтируется внутрь контейнера; владельцем " +
                "должен быть пользователь app (uid 1654), как у каталога ключей: " +
                "docker compose -f deploy/docker-compose.yml run --rm --user root --entrypoint chown " +
                "api -R app:app /app/backups", ex);
        }
    }

    private static InvalidOperationException Fail(string what, string env, string compose) => new(
        $"{what} Приложение не запущено намеренно.\n" +
        $"Задайте целое число в переменной окружения {env} — в развёртывании compose это " +
        $"{compose} в deploy/.env.");
}
