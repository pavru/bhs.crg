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

    /// <summary>
    /// Выдача по запросу. Пустой список означает ровно «ничего не нашлось».
    /// </summary>
    /// <exception cref="BHS.CRG.Application.QualityDocs.SearchUnavailableException">
    /// Движок не смог ответить: таймаут, сетевой сбой, ошибка API. Отличать это от пустой выдачи
    /// обязательно (issue #797): возвращая на отказ пустой список, движок сообщал бы «ничего не
    /// найдено», и полный отказ ВСЕХ движков выглядел бы для пользователя обычным пустым
    /// результатом — притом что искать было негде.
    /// </exception>
    Task<IReadOnlyList<WebHit>> QueryAsync(string query, CancellationToken ct = default);
}
