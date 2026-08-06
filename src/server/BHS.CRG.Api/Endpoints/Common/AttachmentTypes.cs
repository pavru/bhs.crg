namespace BHS.CRG.Api.Endpoints.Common;

/// <summary>
/// Что принимаем во вложения и как это потом отдаём — ОДНОЙ таблицей, и решает в ней РАСШИРЕНИЕ.
///
/// Только форматы, которые браузер показывает как ДАННЫЕ: документы и растровые картинки. Активных
/// форматов здесь быть не должно — вложение загружает один пользователь, а открывает другой, и
/// открывает на домене приложения. Векторная графика для шаблонов идёт своим путём, через ассеты
/// шаблонов (<c>TemplateAssetEndpoints</c>, роль Admin), — вложениям она не нужна.
///
/// <para><b>Почему решает расширение, а не заявленный тип.</b> Заявленный клиентом MIME не
/// подтверждён ничем: браузер берёт его из реестра ОС, и на машине без Office <c>.xlsx</c>
/// объявляется как <c>application/vnd.ms-excel</c>, а <c>.csv</c> — им же. Сверять заявленный тип с
/// расширением значит отвергать такие загрузки, хотя ничего опасного в них нет. А главное — отдача
/// всё равно выводит тип ИЗ РАСШИРЕНИЯ, то есть заявленный тип на судьбу файла не влияет вовсе.
/// Значит ограничивать надо ровно то, что решает: расширение. Проверка выходит и строже (evil.svg,
/// объявленный картинкой, отвергается), и без ложных отказов.</para>
///
/// <para>Таблица одна намеренно. Приём и отдача держали независимые списки, и это делало приём
/// декоративным: расширь один — второй об этом не узнает, а вся защита отдачи держится на их
/// согласии. Теперь расхождение невозможно по построению.</para>
/// </summary>
public static class AttachmentTypes
{
    /// <summary>Расширение → MIME, которым файл принимается и отдаётся.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["xls"] = "application/vnd.ms-excel",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
    };

    private static string ExtensionOf(string? fileName) =>
        Path.GetExtension(fileName ?? "").TrimStart('.');

    /// <summary>
    /// Принимаем ли файл с таким именем. Имя без расширения тоже отвергаем: тип отдачи выводится из
    /// имени, и такой файл лёг бы нечитаемым — отказ полезнее.
    /// </summary>
    public static bool IsAcceptedName(string? fileName) =>
        ByExtension.ContainsKey(ExtensionOf(fileName));

    /// <summary>Растровая ли это картинка — для эндпоинта поля-изображения.</summary>
    public static bool IsImageName(string? fileName) =>
        ByExtension.TryGetValue(ExtensionOf(fileName), out var type) &&
        type.StartsWith("image/", StringComparison.Ordinal);

    /// <summary>
    /// Каким типом отдавать файл с таким именем — он же тип, под которым файл принят и записан.
    /// Всё незнакомое уходит как <c>application/octet-stream</c>: так файл сохраняется, а не
    /// исполняется у того, кто его открыл. Сюда попадают и расширения, загруженные когда-то, пока
    /// список приёма был шире (SVG принимался до версии 0.88.0).
    /// </summary>
    public static string ServeTypeFor(string? fileName) =>
        ByExtension.TryGetValue(ExtensionOf(fileName), out var type) ? type : "application/octet-stream";

    /// <summary>Что перечислить в отказе — расширения, а не MIME: имя файла человек видит.</summary>
    public static string AcceptedExtensions =>
        string.Join(", ", ByExtension.Keys.Select(e => "." + e));
}
