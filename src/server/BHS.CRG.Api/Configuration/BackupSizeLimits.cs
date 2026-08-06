using System.Globalization;
using BHS.CRG.Api.Endpoints.Common;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Предел размера архива резервной копии — настройка развёртывания, а не константа (issue #711).
///
/// Почему это перестало быть константой. Пока копия несла только ассеты шаблонов, её вес мерился
/// единицами мегабайт и упереться в потолок было негде. С решения по issue #687 копия несёт
/// библиотеку документов качества со сканами, и её размер задаётся не конфигурацией, а тем, сколько
/// сертификатов накопилось за годы работы — то есть растёт линейно и без нашего участия. Порог,
/// зашитый в код, означал бы, что установка, переросшая его, чинится только пересборкой образа.
///
/// <para><b>Предел — звено цепи, а не одно число.</b> Путь запроса такой:
/// <code>
/// nginx (client_max_body_size)  →  Kestrel (MaxRequestBodySize)  →  разбор формы  →  наша проверка
/// </code>
/// Поднять значение только здесь — значит получить дефект issue #482 обратно: порог nginx ниже
/// нашего делает наш предел недостижимым, а отказ приходит HTML-страницей nginx без поля
/// <c>error</c>. Поэтому переменная в поставке ОДНА (<c>BACKUP_MAX_ARCHIVE_MB</c>): её читает и API,
/// и контейнер веб-сервера — см. <c>deploy/nginx-body-size.sh</c>, который считает
/// <c>client_max_body_size</c> из неё же, добавляя запас на обвязку multipart.</para>
/// </summary>
public sealed class BackupSizeLimits
{
    /// <summary>Значение по умолчанию: около 390 сертификатов при нынешних ~1,3 МБ на скан.</summary>
    public const int DefaultMb = 500;

    /// <summary>
    /// Ниже этого предел бессмыслен: манифест пустой системы вместе с ассетами шаблонов уже весит
    /// единицы мегабайт, и порог в «1 МБ» отвергал бы собственную свежую копию.
    /// </summary>
    public const int MinMb = 16;

    /// <summary>
    /// Верхняя граница — против описки. Значение попадает в потолок тела запроса, то есть в объём,
    /// который сервер согласен выписать на диск, прежде чем что-то проверять; лишний ноль в
    /// <c>deploy/.env</c> не должен превращаться в такое согласие молча.
    /// </summary>
    public const int MaxMb = 10_240;

    private const string Key = "Backup:MaxArchiveMb";
    private const string EnvName = "Backup__MaxArchiveMb";
    private const string ComposeName = "BACKUP_MAX_ARCHIVE_MB";

    private BackupSizeLimits(int archiveMb) => ArchiveMb = archiveMb;

    public int ArchiveMb { get; }

    /// <summary>Предел самого архива — с ним сверяется загруженный файл.</summary>
    public long ArchiveBytes => ArchiveMb * 1024L * 1024L;

    /// <summary>Предел тела запроса: архив плюс запас на обвязку multipart.</summary>
    public long RequestBytes => ArchiveBytes + UploadLimits.MultipartOverhead;

    public static BackupSizeLimits FromConfiguration(IConfiguration cfg) => Parse(cfg[Key]);

    /// <summary>
    /// Незаданное значение — это <see cref="DefaultMb" />, а не отказ: подавляющему большинству
    /// установок настраивать тут нечего. А вот заданное негодно — именно отказ на старте, в тон
    /// прочим проверкам конфигурации: «500 МБ» и «пять сотен мегабайт» не должны различаться тем,
    /// что второе молча даёт дефолт.
    /// </summary>
    public static BackupSizeLimits Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new BackupSizeLimits(DefaultMb);

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mb))
            throw Fail($"Предел размера резервной копии ({Key}) не число: «{value.Trim()}».");

        if (mb < MinMb || mb > MaxMb)
            throw Fail(
                $"Предел размера резервной копии ({Key}) вне допустимого диапазона: {mb}. " +
                $"Допустимо от {MinMb} до {MaxMb} МБ.");

        return new BackupSizeLimits(mb);
    }

    private static InvalidOperationException Fail(string what) => new(
        $"{what} Приложение не запущено намеренно.\n" +
        $"Задайте целое число мегабайт в переменной окружения {EnvName} — в развёртывании compose " +
        $"это {ComposeName} в deploy/.env. Тем же значением задаётся client_max_body_size у " +
        "веб-сервера, поднимать его отдельно не нужно.");
}
