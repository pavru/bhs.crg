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
}
