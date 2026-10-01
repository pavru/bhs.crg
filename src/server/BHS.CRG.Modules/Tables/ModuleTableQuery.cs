namespace BHS.CRG.Modules.Tables;

/// <summary>
/// Отбор строк таблицы — то же дерево условий, что у наборов данных, но уже ПРОВЕРЕННОЕ ядром
/// (ТЗ CORE-33; задача G1c, issue #1090): колонка существует и спрашивающему открыта, оператор к её
/// виду применим, значения разбираются. Служба модуля получает его готовым и исполняет запросом к
/// своей базе — обычно через <see cref="TableSql{T}" />.
/// </summary>
public abstract record TableFilter;

/// <summary>Группа условий: все разом (<c>Any = false</c>) или любое. Пустая группа ничего не ограничивает.</summary>
public sealed record TableFilterGroup(bool Any, IReadOnlyList<TableFilter> Children) : TableFilter;

/// <summary>Условие по колонке.</summary>
/// <param name="Column">Ключ колонки.</param>
/// <param name="Kind">Вид колонки по объявлению таблицы или схеме типа.</param>
/// <param name="Op">Оператор из общего списка ядра: <c>eq</c>, <c>contains</c>, <c>between</c>, <c>in</c>…</param>
/// <param name="Values">Значения: ни одного, одно, две границы либо список — по оператору.</param>
/// <param name="Matches">
/// То же условие для значения В ПАМЯТИ — правило ядра, а не модуля. Нужно колонке-справочнику
/// (поставщик, состояние оплаты): в базе лежит ссылка, а человек отбирает по названию, и названия
/// сверяются здесь. Так у «содержит» один смысл на обоих исполнителях, а не два похожих.
/// </param>
public sealed record TableFilterCondition(
    string Column, ModuleTableColumnKind Kind, string Op, IReadOnlyList<string> Values,
    Func<string?, bool> Matches) : TableFilter;

/// <summary>Сортировка по колонке.</summary>
public sealed record TableSort(string Column, ModuleTableColumnKind Kind, bool Descending);

/// <summary>Что спрашивают у службы строк.</summary>
/// <param name="Columns">Колонки, которые спрашивающему ОТКРЫТЫ. Остальные служба вправе не считать
/// вовсе; ядро всё равно вычистит их из ответа — гарантия стоит в одном месте, а не в каждой
/// службе.</param>
/// <param name="UserId">От чьего имени читаем: нужен таблице с построчной изоляцией.</param>
/// <param name="Filter">Отбор; null — все строки.</param>
/// <param name="Sort">Сортировка по порядку важности; пусто — порядок таблицы по умолчанию.</param>
/// <param name="Offset">Сколько строк отбора пропустить.</param>
/// <param name="Limit">Сколько строк отдать; null — все (набор данных читает таблицу целиком).</param>
/// <param name="Totals">Колонки, по которым нужен итог, с их видами. Итог — по ВСЕМУ отбору, а не
/// по странице.</param>
public sealed record ModuleTableQuery(
    IReadOnlySet<string> Columns,
    Guid UserId,
    TableFilter? Filter = null,
    IReadOnlyList<TableSort>? Sort = null,
    int Offset = 0,
    int? Limit = null,
    IReadOnlyDictionary<string, ModuleTableColumnKind>? Totals = null);

/// <summary>Страница строк таблицы.</summary>
/// <param name="Rows">Строки страницы.</param>
/// <param name="Count">Сколько строк в отборе ВСЕГО — не на странице.</param>
/// <param name="Totals">Итоги по запрошенным колонкам, по всему отбору.</param>
public sealed record ModuleTablePage(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Count,
    IReadOnlyDictionary<string, TableTotal> Totals);

/// <summary>
/// Итог по колонке — по всему отбору (ТЗ CORE-33).
/// </summary>
/// <param name="Count">Сколько значений учтено: чисел у числа, дат у даты, непустых у остального.</param>
/// <param name="Skipped">Сколько значений НЕ учтено — в клетке лежит не то, что обещает вид колонки
/// («12 шт» в числе). Пустые клетки сюда не входят: пусто — не ошибка. Экран говорит об этом вслух
/// («не учтено N значений: не число»), иначе сумма была бы молча меньше.</param>
/// <param name="Sum">Сумма — только у числа.</param>
/// <param name="Min">Наименьшее: <c>decimal</c> у числа, <c>DateOnly</c> у даты.</param>
/// <param name="Max">Наибольшее.</param>
public sealed record TableTotal(long Count, long Skipped, decimal? Sum = null, object? Min = null, object? Max = null)
{
    /// <summary>Среднее учтённых значений.</summary>
    public decimal? Average => Sum is { } sum && Count > 0 ? sum / Count : null;
}

/// <summary>
/// Служба модуля, которая отбирает строки таблицы (ТЗ CORE-24.1: «строки отбирает та же служба
/// модуля, которая отвечает API»). Значения — по виду колонки: число <c>decimal</c>, дата
/// <c>DateOnly</c>, флаг <c>bool</c>, остальное строкой; пустое — <c>null</c>.
///
/// <para>Отбор, сортировку и страницу исполняет ОНА — запросом к своей базе: общего запроса по
/// произвольным данным ядро не пишет, иначе таблица стала бы вторым обходом изоляции (ТЗ CORE-33).</para>
/// </summary>
public interface IModuleTableRows
{
    Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct);
}
