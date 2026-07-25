namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Чтение данных наборов для внешнего потребителя (issue #415). Отдельный сервис, а не переиспользование
/// <c>IDataSetService</c> напрямую, потому что здесь другой концерн: не редактирование, а достоверность
/// снимка — происхождение, свежесть, полнота выборки и якорь на исходные листы.
///
/// Живёт в Application, чтобы MCP-слой остался ТОНКИМ адаптером (те же правила, что для эндпоинтов):
/// вычисление origin/stale/якоря — доменное знание, ему не место в транспорте.
/// </summary>
public interface IDataSnapshotService
{
    /// <summary>Наборы данных — точка входа: без неё внешний потребитель не узнает идентификаторов.</summary>
    Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(
        string? scope, Guid? scopeId, CancellationToken ct = default);

    /// <summary>Структура набора и его источники, либо null — набора нет.</summary>
    Task<DatasetDetail?> GetDatasetAsync(Guid datasetId, CancellationToken ct = default);

    /// <summary>Источник с колонками и метаданными достоверности, либо null.</summary>
    Task<SourceDetail?> GetSourceAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>Страница строк источника после всей обработки. Лимит ограничивается сверху жёстко —
    /// см. <see cref="MaxRowsPerPage"/>; за пределом страницы выставляется <c>Truncated</c>.</summary>
    Task<RowsPage?> GetRowsAsync(Guid sourceId, int offset, int limit, CancellationToken ct = default);

    /// <summary>Жёсткий потолок строк за один запрос: защищает и от переполнения контекста агента,
    /// и от неявного «получил всё» на большом источнике.</summary>
    static int MaxRowsPerPage => 500;

    static int DefaultRowsPerPage => 200;
}
