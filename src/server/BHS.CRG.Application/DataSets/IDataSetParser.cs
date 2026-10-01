using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Application.DataSets;

public record DataSetColumnInfo(string Name, string[] SampleValues);

public record DataSetSourceInfo(
    string Name,
    string SheetOrPath,
    IReadOnlyList<DataSetColumnInfo> Columns,
    int RowCount,
    // Кандидат-таблица, ещё НЕ распознанная (issue #385): указывает страницу документа для запуска
    // «Распознать таблицу». null — обычный готовый кандидат (создаётся сразу).
    int? FirstPageIndex = null,
    /// <summary>Что про эти данные надо знать до того, как им поверят (issue #626): реестр
    /// поддерева не знает метаданных несобранных комплектов. null — сказать нечего.</summary>
    string? Warning = null,
    /// <summary>Сколько источников на эту консолидацию уже создано (issue #717). Раньше занятый
    /// кандидат просто исчезал из списка, и «добавить второй список документов» не имело входа —
    /// теперь кандидат остаётся видимым со счётчиком. 0 — ещё не создан ни одного.</summary>
    int ExistingCount = 0
);

public record DataSetParseResult(
    IReadOnlyList<DataSetColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    /// <summary>Оговорка к данным — живая, считается вместе со строками (issue #626).</summary>
    string? Warning = null,
    /// <summary>
    /// Какой исход отбора применился — строка из объявления набора (ТЗ CORE-24.3, issue #965).
    ///
    /// <para>Заполняет только поставщик с построчной изоляцией: он один знает, что именно отобрал —
    /// «ваши стройки» или «все стройки». Поставщик без изоляции молчит, и берётся единственная
    /// объявленная строка. Объявил несколько исходов и не выбрал ни одного — отказ: показать первую
    /// значило бы подписать данные не тем текстом, а это хуже отсутствующей подписи.</para>
    /// </summary>
    string? Boundary = null,
    /// <summary>
    /// Виды колонок — у набора, чей поставщик их объявляет (таблица модуля, G1c). null — видов нет
    /// (файл, распознавание), и отбор сравнивает по догадке, как прежде.
    /// </summary>
    DataSetColumnTypes? Types = null
);

/// <summary>
/// Что поставщик знает о своих колонках (ТЗ CORE-33; задача G1c, issue #1090).
/// </summary>
/// <param name="Kinds">Колонка → вид значения (см. <c>TableOperators</c>). Условие по такой колонке
/// сравнивает по виду, а не по догадке — так же, как запрос к базе у экрана таблицы.</param>
/// <param name="Closed">Колонка → причина, по которой она пришла без значений («нет права на суммы»).
/// Отбор по такой колонке отказывает: у пустых клеток он вернул бы «ничего не нашлось», и человек
/// без права на суммы получил бы пустой набор вместо отказа.</param>
public sealed record DataSetColumnTypes(
    IReadOnlyDictionary<string, string> Kinds,
    IReadOnlyDictionary<string, string> Closed);

public interface IDataSetParser
{
    bool CanParse(DataSetFormat format);

    /// <summary>Обнаруживает все логические наборы внутри файла (листы, xpath-пути, json-ключи).</summary>
    Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct);

    /// <summary>
    /// Парсит один конкретный набор по его sheetOrPath.
    /// columnExpressions — опционально (используется XML-парсером): JSON [{name,expr}] с явными
    /// относительными XPath-выражениями колонок; прочие парсеры параметр игнорируют.
    /// </summary>
    Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct);
}
