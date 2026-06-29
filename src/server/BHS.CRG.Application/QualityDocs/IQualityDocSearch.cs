namespace BHS.CRG.Application.QualityDocs;

/// <summary>Кандидат-документ из веб-поиска.</summary>
public record SearchCandidate(string Title, string Url, string Snippet, string Source);

/// <summary>
/// Веб-поиск документов качества с приоритетом по тирам: ФГИС → производитель → общий веб
/// (реализуется через внешний поисковый API с site:-ограничениями).
/// </summary>
public interface IQualityDocSearch
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>Скачивание файла по ссылке (для импорта найденного скана в библиотеку).</summary>
public record FetchedFile(byte[] Bytes, string FileName, string MimeType);

public interface IFileUrlFetcher
{
    Task<FetchedFile> FetchAsync(string url, CancellationToken ct = default);
}

/// <summary>Веб-поиск не настроен (нет ключа) или иная ошибка доступа.</summary>
public class SearchUnavailableException(string message) : Exception(message);
