using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>
/// Пределы окна подсказок (D2, замер #1528): последние действующие записи семьи по времени правки,
/// пока их данные в сумме не больше <see cref="MaxBytes" /> и их не больше <see cref="MaxRecords" />.
/// Предел времени — <see cref="Timeout" />: не уложился — отказ, а не «пусто».
///
/// <para><see cref="MaxLines" /> — предел строк массивов у окна записей-владельцев (issue #1532): цена
/// массива — разбор строк, а не объём (замер #1528: тысяча документов по 40 строк укладывается в 8 МБ
/// и стоит 300 мс). Окно обрывается на записи, где сумма строк читаемых массивов превысила предел.</para>
/// </summary>
public sealed record SuggestionWindow(int MaxRecords, long MaxBytes, TimeSpan Timeout, int MaxLines = 10_000)
{
    /// <summary>Пределы адреса: 1000 записей, 8 МБ, 300 мс, 10 000 строк массивов.</summary>
    public static SuggestionWindow Default { get; } = new(1000, 8L * 1024 * 1024, TimeSpan.FromMilliseconds(300));
}

/// <summary>Значение поля у записи окна — как лежит, с уровнем записи.</summary>
public sealed record SuggestionRow(Guid RecordId, string Value, CatalogScope ScopeLevel, Guid? ScopeId, DateTimeOffset UpdatedAt);

/// <summary>
/// Узкий порт «значения поля у окна записей типов X» — верхнего уровня и по путям внутри записей
/// типов-владельцев (D11, issue #1532). Не очередная часть
/// репозитория объектов: форма запроса здесь — часть решения (окно подзапросом, поле только у записей
/// окна), и общий репозиторий её не выразит.
///
/// <para>Читает только СОБСТВЕННЫЕ значения записи: ссылка <c>_baseRef</c> не разрешается (D7), и
/// унаследованная копия частоту основы не поднимает. Архивные записи в окно не попадают (D6).</para>
///
/// <para>Значение из строк массива приходит строкой ЗАПИСИ-ВЛАДЕЛЬЦА — с её id, уровнем и временем, по
/// одной на разное значение: повтор в пятидесяти строках одного журнала весит одну запись.</para>
/// </summary>
public interface IFieldSuggestionReader
{
    /// <summary>Только верхний уровень.</summary>
    /// <exception cref="SuggestionsTimedOutException">Чтение не уложилось в предел времени.</exception>
    Task<IReadOnlyList<SuggestionRow>> ReadAsync(
        IReadOnlyCollection<Guid> typeIds, string field, SuggestionWindow window, CancellationToken ct = default)
        => ReadAsync(typeIds, field, [], window, ct);

    /// <summary>
    /// Верхний уровень <paramref name="typeIds" /> и пути <paramref name="nested" /> — одним запросом, под
    /// одним пределом времени. Окна у них свои: строки журналов не вытесняют записи верхнего уровня.
    /// </summary>
    /// <exception cref="SuggestionsTimedOutException">Чтение не уложилось в предел времени.</exception>
    Task<IReadOnlyList<SuggestionRow>> ReadAsync(
        IReadOnlyCollection<Guid> typeIds, string field, IReadOnlyList<NestedSource> nested,
        SuggestionWindow window, CancellationToken ct = default);
}

/// <summary>
/// Подсказки не успели (D10): отказ, а не пустой список. Пустой ответ на таймауте выглядел бы как
/// «ничего не вводили», и поле молча перестало бы подсказывать ровно там, где записей больше всего.
/// </summary>
public sealed class SuggestionsTimedOutException(Exception inner)
    : Exception("Подсказки сейчас не успевают: база не уложилась в предел времени. Повторите позже.", inner);
