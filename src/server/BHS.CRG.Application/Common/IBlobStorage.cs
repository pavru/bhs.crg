namespace BHS.CRG.Application.Common;

public interface IBlobStorage
{
    Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);
    Task DeleteAsync(string blobPath, CancellationToken ct = default);
    /// <summary>Restores a blob to its exact original path (used by backup restore).</summary>
    Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Размер объекта в байтах; <c>null</c> — хранилище ответило, что объекта НЕТ (issue #711).
    ///
    /// Заведён ради оценки веса резервной копии: сканы библиотеки качества лежат в архиве без
    /// сжатия, поэтому сумма их размеров и есть их вклад в копию — и узнать её нужно, НЕ выкачивая
    /// содержимое. Отсутствие объекта здесь не отказ: экспорт такой блоб тоже пропускает, лишь
    /// записывая предупреждение, и оценка обязана вести себя так же, иначе она бы завышала.
    ///
    /// <para>⚠️ Недоступное хранилище — отказ, а не <c>null</c> (issue #1427): до хранилища не
    /// достучаться, нет прав, нет бакета, истёк срок — <see cref="BlobStorageUnavailableException" />.
    /// Прежде это выглядело одинаково, и лежащее хранилище давало оценку копии «все файлы пропали»
    /// вместо отказа.</para>
    /// </summary>
    Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Какие из путей отсутствуют — тот же ответ, что <see cref="GetSizeAsync" /> <c>null</c>, но о
    /// многих путях сразу (issue #1439). Недоступное хранилище — тот же отказ
    /// <see cref="BlobStorageUnavailableException" />, на первом же пути.
    ///
    /// <para>Заведён ради проверки файлов после восстановления: у копии, снятой при недоступном
    /// хранилище, путей вне архива тысячи, и вопрос о каждом по одному шёл десятки секунд внутри
    /// HTTP-запроса. Умолчание здесь — тот же вопрос по одному; обёртка с реестром отвечает быстрее
    /// (<c>RegisteredBlobStorage</c>): незарегистрированные пути отсеивает одним запросом к базе.</para>
    /// </summary>
    async Task<IReadOnlySet<string>> FindAbsentAsync(IEnumerable<string> blobPaths, CancellationToken ct = default)
    {
        var absent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in blobPaths.Distinct(StringComparer.Ordinal))
            if (await GetSizeAsync(path, ct) is null) absent.Add(path);
        return absent;
    }
}

/// <summary>
/// Хранилище не ответило на запрос об объекте — или ответило отказом, но не «такого объекта нет»
/// (issue #1427): связи нет, ключ не принят, бакета нет, ошибка сервера.
///
/// Свой тип, а не то, что бросил клиент хранилища, потому что ловить приходится ровно этот отказ, и
/// ничей больше. Клиент бросает что попало: отказ в доступе — свой тип, обрыв связи —
/// <c>HttpRequestException</c>, а на 500 без тела (у ответа на HEAD тела не бывает) клиент Minio
/// 7.0.0 падает <c>NullReferenceException</c>. Ловить их поимённо — пропустить следующий; ловить
/// всё подряд — выдать за «хранилище не ответило» и сбой базы в реестре, и битый путь.
///
/// Текст — не для человека: в нём путь объекта, и наружу он не уходит (это не
/// <c>DomainException</c>). Что сказать человеку, решает тот, кто отказ поймал; причину — в журнал.
/// </summary>
public class BlobStorageUnavailableException(string message, Exception inner) : Exception(message, inner);
