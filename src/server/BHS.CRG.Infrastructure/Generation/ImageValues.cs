using System.Text.Json.Nodes;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Общие правила распознавания значений полей-изображений (issue #246). Значение бывает двух форм:
///  • голая data-URI строка (<c>data:image/...;base64,...</c>) — легаси / только что загруженная картинка;
///  • объект <c>{ src: data-URI, width?, height?, align?, fit? }</c> — размер/выравнивание задаются в
///    инстансе (перенесены из схемы типа). Служебные ключи размера — те же, что прежде брались из схемы.
/// Обе формы понимают и материализатор Typst, и разовая миграция размеров.
/// </summary>
public static class ImageValues
{
    public static readonly string[] OptionKeys = ["width", "height", "align", "fit"];

    /// <summary>data-URI картинки: <c>data:image/*;base64,...</c>.</summary>
    public static bool IsDataImage(string? s) =>
        s is not null
        && s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        && s.Contains(";base64,", StringComparison.OrdinalIgnoreCase);

    /// <summary>Объект-значение картинки: есть строковый <c>src</c> с data-URI. Возвращает сам src.</summary>
    public static bool TryGetImageObjectSrc(JsonObject obj, out string src)
    {
        src = "";
        if (obj["src"] is JsonValue v && v.TryGetValue<string>(out var s) && IsDataImage(s))
        {
            src = s;
            return true;
        }
        return false;
    }
}
