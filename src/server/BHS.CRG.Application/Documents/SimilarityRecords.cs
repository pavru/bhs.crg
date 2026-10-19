using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Documents;

/// <summary>Лежащие записи глазами сверки — то, что получают <see cref="NomenclatureSimilarity" />.</summary>
/// <param name="Titles">Заголовки полей ключа по виду записи, в порядке ключа, — для причины «совпадает
/// артикул «…»» у <see cref="NomenclatureSimilarity.ForLine" />.</param>
/// <param name="Unreadable">Сколько записей сверить НЕ УДАЛОСЬ (основа удалена, цепочка замкнулась). В
/// <see cref="Records" /> их нет: о них нельзя сказать ни «совпала», ни «не совпала».</param>
public sealed record SimilarityRecords(
    IReadOnlyList<SimilarRecord> Records, IReadOnlyDictionary<Guid, IReadOnlyList<string>> Titles, int Unreadable);

/// <summary>
/// Загрузчик записей сверки (issue #1514) — ОДИН на шаг «Новая позиция» и на подбор для строки бумаги.
///
/// <para>Зачем один: окно заведения сказало бы «похожих нет» о позиции, которую подбор находит, — или
/// наоборот, — стоило бы двум загрузчикам разойтись в одном: в наследовании от основы, в альтернативных
/// именах, в счёте нечитаемых. Значения полей ключа отдаёт запрос ядра
/// (<see cref="CommonDataFieldValuesQuery" />): он разрешает наследование тем же правилом, что печать.</para>
///
/// <para>Архивные записи — на месте, с признаком: архивный двойник — тоже двойник, а подбору нужно их
/// число.</para>
///
/// <para>Кэша нет НАРОЧНО: позиция, заведённая шагом «Новая», обязана быть в кандидатах при следующем
/// открытии окна.</para>
/// </summary>
public interface ISimilarityRecordsLoader
{
    /// <param name="family">Виды, записи которых сверяются: семейство справочника.</param>
    /// <param name="identity">Поля ключа, по которым сверяется КАЖДАЯ запись, — у шага «Новая позиция»
    /// это ключ выбранного вида, и запись подтипа, у которой заполнены свои поля ключа сверх него,
    /// помечена <see cref="SimilarRecord.OwnKey" />. <c>null</c> — у каждой записи ключ её СОБСТВЕННОГО
    /// вида: у строки бумаги вида нет, и сравнивать запись есть с чем только по её ключу. Запись вида без
    /// ключа идентичности тогда сверяется названием записи: поиск в том же окне её находит, и подбор,
    /// молча её пропустивший, говорил бы «подходящих нет» о лежащей позиции.</param>
    Task<SimilarityRecords> LoadAsync(
        IReadOnlyDictionary<Guid, DocumentType> all, IReadOnlyList<DocumentType> family,
        IReadOnlyList<string>? identity, CancellationToken ct = default);
}
