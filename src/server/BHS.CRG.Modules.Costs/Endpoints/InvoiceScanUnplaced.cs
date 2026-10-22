using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Прочитанное, что в счёт не легло, — с причиной (ТЗ COST-8, issue #1484).
///
/// <para><b>Почему список, а не замечания.</b> Профиль распознавания при сохранении с полями счёта не
/// сверяется (решение владельца: он кормит и наборы данных), и единственное место, где видна ошибка —
/// опечатка в ключе, колонка без поля, отказ охраны, — итог распознавания в счёте. Замечаниями это
/// говорилось не всегда: сведение о колонках строк терялось, когда строки уходили в предложение. Список
/// собирается при каждом завершённом распознавании, при любом исходе строк.</para>
///
/// <para>Слова причин — на клиенте, по коду: сервер называет только код и подробность.</para>
/// </summary>
/// <param name="Place"><see cref="InvoiceScanUnplaced.Header" /> или <see cref="InvoiceScanUnplaced.Line" />.</param>
/// <param name="Row">Номер строки в прочитанной таблице файла, с 1; только у строк.</param>
/// <param name="Key">Ключ профиля как прочитан — без обрезки: опечатка с пробелом видна.</param>
/// <param name="Value">Прочитанное, не длиннее 40 знаков.</param>
/// <param name="Reason">Код причины — константа <see cref="InvoiceScanUnplaced" />.</param>
/// <param name="Detail">Причина поля у <see cref="InvoiceScanUnplaced.NotRecognizable" />, текст отказа у
/// <see cref="InvoiceScanUnplaced.Guard" />, ключ-владелец у <see cref="InvoiceScanUnplaced.DuplicateKey" />.</param>
public sealed record UnplacedRead(string Place, int? Row, string Key, string Value, string Reason, string? Detail = null);

/// <summary>Коды места и причины записи <see cref="UnplacedRead" /> и её хранение.</summary>
public static class InvoiceScanUnplaced
{
    public const string Header = "header";
    public const string Line = "line";

    /// <summary>У счёта (строки счёта) нет поля с таким ключом.</summary>
    public const string NoField = "no-field";

    /// <summary>Поле есть, но распознаванием не заполняется (файл, ссылка, срок по правилу…).</summary>
    public const string NotRecognizable = "not-recognizable";

    /// <summary>Значение не разобрано как значение поля — в предложениях, впишите вручную.</summary>
    public const string Unparsed = "unparsed";

    /// <summary>Охрана записи отказала — значение в предложениях.</summary>
    public const string Guard = "guard";

    /// <summary>Поле занято значением человека — прочитанное в предложениях.</summary>
    public const string Occupied = "occupied";

    /// <summary>Ключ повторяет другой: поле уже заполнено им.</summary>
    public const string DuplicateKey = "duplicate-key";

    public static readonly IReadOnlyList<string> Reasons = [NoField, NotRecognizable, Unparsed, Guard, Occupied, DuplicateKey];

    public static UnplacedRead InHeader(string key, string text, string reason, string? detail = null) =>
        new(Header, null, key, InvoiceScanLines.Short(text), reason, detail);

    public static UnplacedRead InLine(int row, string key, string text, string reason, string? detail = null) =>
        new(Line, row, key, InvoiceScanLines.Short(text), reason, detail);

    /// <summary>
    /// Хранится и отдаётся одной формой — именами API (camelCase): вид распознавания читает записанное
    /// обратно в записи, а не передаёт JSON как лежит.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonDocument Stored(IReadOnlyList<UnplacedRead> unplaced) => JsonSerializer.SerializeToDocument(unplaced, Json);

    /// <summary><c>null</c> — распознавание до issue #1484: «не знаем», а не «пусто».</summary>
    public static IReadOnlyList<UnplacedRead>? Shown(JsonDocument? stored) =>
        stored?.Deserialize<UnplacedRead[]>(Json);
}
