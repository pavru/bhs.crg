namespace BHS.CRG.Application.Common;

public interface IBlobStorage
{
    Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);
    Task DeleteAsync(string blobPath, CancellationToken ct = default);
    /// <summary>Restores a blob to its exact original path (used by backup restore).</summary>
    Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Размер объекта в байтах; <c>null</c> — объекта нет или он недоступен (issue #711).
    ///
    /// Заведён ради оценки веса резервной копии: сканы библиотеки качества лежат в архиве без
    /// сжатия, поэтому сумма их размеров и есть их вклад в копию — и узнать её нужно, НЕ выкачивая
    /// содержимое. Отсутствие объекта здесь не отказ: экспорт такой блоб тоже пропускает, лишь
    /// записывая предупреждение, и оценка обязана вести себя так же, иначе она бы завышала.
    /// </summary>
    Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default);
}
