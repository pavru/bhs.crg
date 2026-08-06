namespace BHS.CRG.Api.Endpoints.Common;

/// <summary>
/// Что принимаем во вложения и как это потом отдаём — ОДНОЙ таблицей.
///
/// Только форматы, которые браузер показывает как ДАННЫЕ: документы и растровые картинки. Активных
/// форматов здесь быть не должно — вложение загружает один пользователь, а открывает другой, и
/// открывает на домене приложения. Векторная графика для шаблонов идёт своим путём, через ассеты
/// шаблонов (<c>TemplateAssetEndpoints</c>, роль Admin), — вложениям она не нужна.
///
/// Таблица одна намеренно. Приём сверял заявленный клиентом тип, отдача выводила тип из расширения
/// имени, и списки жили порознь: расширь один — второй об этом не узнает, а вся защита отдачи
/// держится именно на согласии этих двух. Теперь расхождение невозможно по построению.
/// </summary>
public static class AttachmentTypes
{
    /// <summary>
    /// MIME → расширения, которыми он может называться. Первое в списке — основное, остальные
    /// равноправные написания (<c>.jpg</c> и <c>.jpeg</c>).
    /// </summary>
    private static readonly Dictionary<string, string[]> ByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ["pdf"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ["docx"],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ["xlsx"],
        ["application/vnd.ms-excel"] = ["xls"],
        ["image/png"] = ["png"],
        ["image/jpeg"] = ["jpg", "jpeg"],
        ["image/gif"] = ["gif"],
        ["image/webp"] = ["webp"],
    };

    /// <summary>Обратная сторона той же таблицы: расширение → MIME, которым файл отдаётся.</summary>
    private static readonly Dictionary<string, string> ByExtension =
        ByType.SelectMany(kv => kv.Value.Select(ext => (ext, kv.Key)))
              .ToDictionary(p => p.ext, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Принимаем ли такой заявленный тип.</summary>
    public static bool IsAccepted(string? contentType) =>
        contentType is not null && ByType.ContainsKey(contentType);

    /// <summary>Растровая ли это картинка — для эндпоинта поля-изображения.</summary>
    public static bool IsImage(string? contentType) =>
        IsAccepted(contentType) && contentType!.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Согласуется ли расширение имени с заявленным типом.
    ///
    /// Проверка нужна потому, что заявленный тип приходит от клиента и ничем не подтверждён:
    /// не-браузерный клиент кладёт <c>evil.svg</c>, объявив его <c>image/png</c>, — белый список
    /// приёма против такого не даёт ничего. Само по себе это ещё не исполнение (отдача выводит тип
    /// из расширения и вернёт <c>application/octet-stream</c>), но тогда ВСЯ защита держится на
    /// карте расширений отдачи, то есть на одном рубеже вместо двух.
    ///
    /// Имя без расширения тоже отвергаем: такой файл не отдать заявленным типом, и молча положить
    /// его значит завести вложение, которое нельзя открыть, — отказ полезнее.
    /// </summary>
    public static bool ExtensionMatches(string? fileName, string? contentType)
    {
        if (contentType is null || !ByType.TryGetValue(contentType, out var allowed)) return false;
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && allowed.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Расширения, которыми можно назвать файл заявленного типа — для текста отказа.</summary>
    public static string ExtensionsFor(string? contentType) =>
        contentType is not null && ByType.TryGetValue(contentType, out var allowed)
            ? string.Join(", ", allowed.Select(e => "." + e))
            : "";

    /// <summary>
    /// Каким типом отдавать файл с таким именем. Всё незнакомое уходит как
    /// <c>application/octet-stream</c>: так файл сохраняется, а не исполняется у того, кто его
    /// открыл. Сюда же попадают расширения, загруженные когда-то, пока список приёма был шире.
    /// </summary>
    public static string ServeTypeFor(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName).TrimStart('.'), out var type)
            ? type
            : "application/octet-stream";
}
