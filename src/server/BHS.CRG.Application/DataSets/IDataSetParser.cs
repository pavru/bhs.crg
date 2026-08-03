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
    string? Warning = null
);

public record DataSetParseResult(
    IReadOnlyList<DataSetColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    /// <summary>Оговорка к данным — живая, считается вместе со строками (issue #626).</summary>
    string? Warning = null
);

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
