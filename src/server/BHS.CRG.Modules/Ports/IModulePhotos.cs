namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Подготовка фото для модуля (work-05, ТЗ WORK-33, WORK-55, CORE-34): файл и лимиты на входе —
/// сжатый JPEG, превью, размеры, геометка и время съёмки на выходе. Либо отказ с причиной.
///
/// <para><b>Зачем служба ядра, а не сжатие на клиенте.</b> Фото приходят и с телефона, и из браузера.
/// Сжатие только на клиенте пропустило бы веб-загрузку как есть, и в хранилище лёг бы оригинал на
/// 8–15 МБ. Поэтому перекодирование здесь ВСЕГДА — даже фото в пределах длинной стороны: только так
/// применяется поворот по EXIF и снимаются метаданные снимка (модель и серийный номер устройства).</para>
///
/// <para><b>Служба в хранилище не пишет.</b> Готовые байты модуль кладёт сам, своим портом файлов
/// (<see cref="IModuleBlobs" />). Отказ лимита поэтому не оставляет ничего — ни объекта, ни записи в
/// реестре блобов.</para>
///
/// <para><b>Лимиты передаёт модуль.</b> Порт общий, а пределы у модулей разные. Модуль читает их из
/// своих настроек (<see cref="IModuleSettings" />) на каждом запросе — и смена настройки действует со
/// следующей загрузки.</para>
/// </summary>
public interface IModulePhotos
{
    /// <summary>
    /// Подготовить фото. Поток читается не дальше <see cref="PhotoLimits.MaxBytes" /> + 1 байта:
    /// файл сверх предела отвергается, не дочитанным.
    /// </summary>
    Task<PhotoResult> PrepareAsync(Stream content, PhotoLimits limits, CancellationToken ct = default);
}

/// <summary>Пределы фото, которые задаёт модуль.</summary>
/// <param name="MaxBytes">Предел файла в байтах — до чтения целиком.</param>
/// <param name="MaxSide">Длинная сторона сохраняемого JPEG; меньшее фото не увеличивается.</param>
/// <param name="PreviewSide">Длинная сторона превью.</param>
/// <param name="MaxPixels">Предел площади по заголовку, до декодирования: защита от разжатия в
/// гигапиксели (PNG на 2 МБ размером 20000×20000 разворачивается в 1,6 ГБ памяти).</param>
public sealed record PhotoLimits(long MaxBytes, int MaxSide, int PreviewSide, long MaxPixels)
{
    /// <summary>Потолок площади по умолчанию — тот же, что у вложений ядра.</summary>
    public const long DefaultMaxPixels = 64_000_000;
}

/// <summary>
/// Итог подготовки: <see cref="PreparedPhoto" /> или <see cref="PhotoRefusal" />.
///
/// <para>Отказ — значение, а не исключение и не <c>null</c>: модуль обязан ответить на него человеку, и
/// перепутать его с успехом нельзя.</para>
/// </summary>
public abstract record PhotoResult;

/// <summary>Подготовленное фото: всегда JPEG, уже повёрнутое по EXIF и без метаданных снимка.</summary>
/// <param name="Image">JPEG длинной стороной не больше <see cref="PhotoLimits.MaxSide" />.</param>
/// <param name="Preview">JPEG длинной стороной не больше <see cref="PhotoLimits.PreviewSide" />.</param>
/// <param name="Width">Ширина <paramref name="Image" /> — после поворота.</param>
/// <param name="Height">Высота <paramref name="Image" /> — после поворота.</param>
/// <param name="Geo">Геометка снимка или <c>null</c>: в файле её нет, а модуль хранит её колонками.</param>
/// <param name="TakenAt">Время съёмки из EXIF. Пояс — из снимка, если он записан; иначе — пояс компании.</param>
public sealed record PreparedPhoto(
    byte[] Image, byte[] Preview, int Width, int Height, PhotoGeo? Geo, DateTimeOffset? TakenAt) : PhotoResult;

/// <summary>Отказ: причина кодом — для решения модуля, текстом — для человека.</summary>
public sealed record PhotoRefusal(PhotoRefusalCode Code, string Text) : PhotoResult;

/// <summary>Почему фото не принято.</summary>
public enum PhotoRefusalCode
{
    /// <summary>Файл больше предела в байтах.</summary>
    TooLarge,

    /// <summary>Изображение больше предела в пикселях — по заголовку.</summary>
    TooManyPixels,

    /// <summary>Файл не распознан как изображение: PDF, повреждённый файл, неизвестный формат.</summary>
    NotAnImage,
}

/// <summary>Место съёмки из EXIF: широта и долгота в градусах (юг и запад — со знаком минус), высота в метрах.</summary>
public sealed record PhotoGeo(double Lat, double Lon, double? Alt);
