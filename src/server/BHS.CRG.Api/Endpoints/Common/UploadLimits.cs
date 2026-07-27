namespace BHS.CRG.Api.Endpoints.Common;

/// <summary>
/// Пределы размера загружаемых файлов — одной точкой (issue #482).
///
/// До этого числа были разбросаны по эндпоинтам (50/50/20/нет/нет), а главное — **заявленные пределы
/// были недостижимы**: Kestrel по умолчанию режет тело запроса на 30 000 000 байт, то есть проверка
/// «файл превышает 50 МБ» не срабатывала никогда, а пользователь вместо неё получал 500 с
/// английским текстом фреймворка «Request body too large».
///
/// Поэтому <see cref="Max"/> прокидывается в конфигурацию Kestrel и формы: заявленное значение
/// обязано быть настоящим, иначе проверка — украшение.
/// </summary>
public static class UploadLimits
{
    private const long Mb = 1024 * 1024;

    /// <summary>Вложение документа.</summary>
    public const long Attachment = 50 * Mb;

    /// <summary>Скан печатной формы.</summary>
    public const long PrintForm = 50 * Mb;

    /// <summary>Графика и шрифты шаблонов (#62) — заметно меньше: это элементы вёрстки, не сканы.</summary>
    public const long TemplateAsset = 20 * Mb;

    /// <summary>Файл набора данных: PDF-альбомы, XLSX, ZIP выгрузки.</summary>
    public const long DataSetFile = 50 * Mb;

    /// <summary>
    /// Архив восстановления. Заметно больше прочих: бэкап несёт блобы ассетов и растёт вместе с ними
    /// (на этой системе уже 9 МБ), а упереться в потолок он должен не молча и не на середине.
    /// </summary>
    public const long BackupArchive = 500 * Mb;

    /// <summary>Наибольший из пределов — им настраиваются Kestrel и разбор multipart-формы.</summary>
    public const long Max = BackupArchive;

    /// <summary>Человеческий отказ, если файл больше предела; иначе null.</summary>
    public static IResult? Exceeded(IFormFile? file, long limit) =>
        file is not null && file.Length > limit ? TooLarge(limit) : null;

    /// <summary>
    /// 413, а не 400: размер — это про транспорт, и код должен отличать «файл не такой» от «файл
    /// слишком большой», иначе клиенту нечем выбрать между «поправьте формат» и «разбейте файл».
    /// </summary>
    public static IResult TooLarge(long limit) =>
        Results.Json(new { error = $"Файл превышает {limit / Mb} МБ" }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
