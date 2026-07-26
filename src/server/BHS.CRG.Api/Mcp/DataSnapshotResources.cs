using System.ComponentModel;
using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// URI-адресуемое чтение тех же данных (#415): удобно, когда пользователь хочет ПРИКРЕПИТЬ конкретный
/// набор или источник к диалогу как контекст, а не полагаться на самостоятельный вызов инструмента.
///
/// Делегирует в тот же <see cref="IDataSnapshotService"/> — дублирования логики нет, только вторая
/// форма адресации. Строки намеренно НЕ отдаются ресурсом: они страничные, а ресурс не выражает
/// «за этой страницей есть ещё» — риск тихой неполноты, которого мы избегаем (см. get_rows).
/// </summary>
[McpServerResourceType]
public class DataSnapshotResources(IDataSnapshotService snapshots)
{
    [McpServerResource(UriTemplate = "bhs://dataset/{datasetId}", Name = "dataset",
        Title = "Набор данных", MimeType = "application/json")]
    [Description("Структура набора данных: источники, число строк, происхождение, признак устаревания.")]
    public async Task<ResourceContents> GetDatasetAsync(Guid datasetId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://dataset/{datasetId}", await snapshots.GetDatasetAsync(datasetId, ct));

    [McpServerResource(UriTemplate = "bhs://source/{sourceId}", Name = "source",
        Title = "Источник данных", MimeType = "application/json")]
    [Description("Источник: колонки с примерами, происхождение, свежесть, якорь на листы исходного PDF.")]
    public async Task<ResourceContents> GetSourceAsync(Guid sourceId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://source/{sourceId}", await snapshots.GetSourceAsync(sourceId, ct));
}
