namespace BHS.CRG.Infrastructure.Search;

/// <summary>Одна выдача поискового движка (без привязки к тиру).</summary>
public record WebHit(string Title, string Url, string Snippet);

/// <summary>
/// Низкоуровневый поисковый движок (Serper/Яндекс/…). Тиринг (ФГИС→производитель→веб)
/// и слияние выполняет оркестратор <see cref="TieredWebSearch"/>.
/// </summary>
public interface IWebSearchEngine
{
    string Name { get; }
    Task<IReadOnlyList<WebHit>> QueryAsync(string query, CancellationToken ct = default);
}
