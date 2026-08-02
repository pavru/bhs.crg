using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Единая точка извлечения строк источника: extraction → computed columns (transformation) →
/// row filter → sort. Через неё смотрят на данные генерация, превью, экспорт, материализация,
/// MCP-срез и сверка — расширение способа извлечения (новый вид источника) делается здесь один раз.
/// </summary>
public interface IDataSetRowLoader
{
    /// <summary>Требует source.File загруженным (.Include) заранее у вызывающего кода.</summary>
    Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        DataSetSource source, CancellationToken ct);

    /// <summary>
    /// То же, но с числом строк ДО обработки (issue #592). Отдельный метод, а не второй проход:
    /// сырьё известно только внутри загрузки, а извлечь его повторно значит второй раз скачать и
    /// разобрать файл — либо второй раз сходить к системному провайдеру.
    /// </summary>
    Task<LoadedRows> LoadAsync(DataSetSource source, CancellationToken ct);
}

/// <summary>
/// Строки источника вместе с тем, сколько их было до обработки.
/// </summary>
/// <param name="RawRowCount">Сколько строк дал сам источник — до вычисляемых колонок, фильтра и
/// сортировки. Расхождение с <c>Rows.Count</c> и есть работа фильтра: внешний читатель, увидевший
/// только одно из двух чисел, решает, что вторая половина строк потерялась.</param>
public record LoadedRows(
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, int RawRowCount);
