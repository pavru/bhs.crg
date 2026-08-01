using System.Text.Json.Nodes;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Общие правила распознавания значений полей-изображений (issue #246). Значение бывает трёх форм:
///  • голая data-URI строка (<c>data:image/...;base64,...</c>) — легаси / только что загруженная картинка;
///  • объект <c>{ src: data-URI, width?, height?, align?, fit? }</c> — размер/выравнивание задаются в
///    инстансе (перенесены из схемы типа). Служебные ключи размера — те же, что прежде брались из схемы;
///  • объект <c>{ $type:"image", blobPath, width?, ... }</c> — картинка живёт в блоб-хранилище (issue #522).
/// Все формы понимают и материализатор Typst, и разовая миграция размеров.
/// <para>
/// Читать data-URI мы не перестанем НИКОГДА, даже после переезда: восстановление бэкапа заново
/// впрыскивает старую форму, а архивы восстановимы неограниченно долго — срок был бы объявлен, но не
/// обеспечен. Проект уже принимал это решение дважды (легаси-строка здесь же, легаси-<c>options</c>
/// в <c>EnumType</c>).
/// </para>
/// </summary>
public static class ImageValues
{
    public static readonly string[] OptionKeys = ["width", "height", "align", "fit"];

    /// <summary>data-URI картинки: <c>data:image/*;base64,...</c>.</summary>
    public static bool IsDataImage(string? s) =>
        s is not null
        && s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        && s.Contains(";base64,", StringComparison.OrdinalIgnoreCase);

    /// <summary>Дискриминатор узла-картинки в блоб-хранилище. Не <c>"file"</c> — тот занят вложениями.</summary>
    public const string BlobTypeMarker = "image";

    /// <summary>
    /// Узел-ссылка на блоб: <c>{ $type:"image", blobPath }</c> (issue #522). Возвращает путь блоба.
    /// </summary>
    public static bool TryGetImageBlobPath(JsonObject obj, out string blobPath)
    {
        blobPath = "";
        // TryGetValue, а не GetValue: этот разбор идёт по ВСЕМУ контексту генерации, включая данные
        // наборов и плагинов, а там «$type» может оказаться числом или объектом — GetValue бросил бы
        // InvalidOperationException и уронил генерацию PDF вместо того, чтобы пропустить чужой узел
        // (issue #532).
        if (obj["$type"] is not JsonValue t || !t.TryGetValue<string>(out var marker) || marker != BlobTypeMarker)
            return false;
        if (obj["blobPath"] is not JsonValue v || !v.TryGetValue<string>(out var p) || p.Length == 0) return false;
        blobPath = p;
        return true;
    }

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
