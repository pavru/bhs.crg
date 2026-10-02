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

/// <summary>Вопросы к дереву отбора, одинаковые у всех модулей.</summary>
public static class TableFilters
{
    /// <summary>Отрицания: «не подошло под положительное» — у перечня это «нет ни одного такого».</summary>
    public static bool IsNegative(string op) => op is "neq" or "not_in" or "not_contains";

    /// <summary>«Значения нет» и «значение есть» — под обоими именами.</summary>
    public static bool IsPresence(string op) => op is "is_empty" or "is_not_empty" or "is_null" or "is_not_null";

    /// <summary>
    /// Условия, которыми отбор НАЗЫВАЕТ значения колонки: «объект равен X», «объект из списка». Пусто —
    /// отбор колонку не называет, и её значениями строки не ограничены.
    ///
    /// <para>Нужно колонке, чей смысл зависит от отбора (<see cref="ModuleTableColumn.DependsOnFilter" />):
    /// долю счёта считают на те объекты, которые отбор назвал. Поэтому правило строгое — названо только
    /// то, без чего строка в отбор НЕ ПОПАЛА БЫ:</para>
    /// <list type="bullet">
    /// <item>положительное условие по колонке на пути из одних групп «все разом»;</item>
    /// <item>группа «любое», в которой КАЖДАЯ ветка называет колонку: «объект X или объект Y»;</item>
    /// <item>отрицание («объект не X»), «пусто» и группа «объект X или поставщик Y» не называют ничего:
    /// под ними в отборе есть строки, у которых на X нет ни одной части, и «доля на X» у них — выдумка.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<TableFilterCondition> Naming(TableFilter? filter, string column)
    {
        switch (filter)
        {
            case TableFilterCondition condition:
                return condition.Column == column && !IsNegative(condition.Op) && !IsPresence(condition.Op)
                    ? [condition]
                    : [];
            case TableFilterGroup { Any: false } all:
                return [.. all.Children.SelectMany(c => Naming(c, column))];
            case TableFilterGroup { Children.Count: > 0 } any:
                var branches = any.Children.Select(c => Naming(c, column)).ToList();
                return branches.All(b => b.Count > 0) ? [.. branches.SelectMany(b => b)] : [];
            default:
                return [];
        }
    }
}

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
/// <param name="Row">Ключ ОДНОЙ строки (<see cref="ModuleTablePage.Keys" />): отдать только её — и
/// только если она в отборе. Так экран открывает строку в боковой панели (ТЗ CORE-33; задача G1e,
/// issue #1092) — под ТЕМ ЖЕ отбором, что и таблицу: колонка, чей смысл зависит от отбора, в панели
/// обязана значить то же, что в клетке. null — все строки отбора.
///
/// <para>⚠️ Ключ, которого служба не понимает, — «такой строки нет», а не все строки: ядро сверяет
/// ключи ответа с запрошенным и на чужой строке останавливается.</para></param>
public sealed record ModuleTableQuery(
    IReadOnlySet<string> Columns,
    Guid UserId,
    TableFilter? Filter = null,
    IReadOnlyList<TableSort>? Sort = null,
    int Offset = 0,
    int? Limit = null,
    IReadOnlyDictionary<string, ModuleTableColumnKind>? Totals = null,
    string? Row = null);

/// <summary>Страница строк таблицы.</summary>
/// <param name="Rows">Строки страницы.</param>
/// <param name="Count">Сколько строк в отборе ВСЕГО — не на странице.</param>
/// <param name="Totals">Итоги по запрошенным колонкам, по всему отбору.</param>
/// <param name="Notes">Что колонка значит ПОД ЭТИМ ОТБОРОМ — подписью к её заголовку: «доля: Комарова
/// 36». Только у колонок, объявленных зависящими от отбора
/// (<see cref="ModuleTableColumn.DependsOnFilter" />), и только когда смысл действительно сменился.</param>
/// <param name="Keys">Ключи строк — по одному на строку, в том же порядке: чем строку назвать, чтобы
/// прочитать её снова (<see cref="ModuleTableQuery.Row" />). Отдельным списком, а не ключом в самой
/// строке: строка — это значения колонок, и набор данных, читающий ту же таблицу, получил бы
/// служебный ключ лишней колонкой. null — служба ключей не называет, и строка на экране не
/// открывается: это сказано, а не угадано по номеру строки на странице.</param>
public sealed record ModuleTablePage(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Count,
    IReadOnlyDictionary<string, TableTotal> Totals,
    IReadOnlyDictionary<string, string>? Notes = null,
    IReadOnlyList<string>? Keys = null);

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
/// <param name="Note">Что итог значит ПОД ЭТИМ ОТБОРОМ — подписью под ним: «период — по дате счёта, не
/// по оплате» (задача G4, issue #1097). Отдельно от подписи колонки (<see cref="ModuleTablePage.Notes" />):
/// та меняет смысл КЛЕТОК и положена только колонке, объявленной зависящей от отбора, а оговорка об
/// отборе относится к нижней строке любой колонки — клетка «Сумма к оплате» под отбором периода значит
/// то же, а её итог читают как «столько потрачено за период». null — оговорки нет.</param>
public sealed record TableTotal(
    long Count, long Skipped, decimal? Sum = null, object? Min = null, object? Max = null, string? Note = null)
{
    /// <summary>Среднее учтённых значений.</summary>
    public decimal? Average => Sum is { } sum && Count > 0 ? sum / Count : null;
}

/// <summary>
/// Служба модуля, которая отбирает строки таблицы (ТЗ CORE-24.1: «строки отбирает та же служба
/// модуля, которая отвечает API»). Значения — по виду колонки: число <c>decimal</c>, дата
/// <c>DateOnly</c>, флаг <c>bool</c>, перечень — списком строк (пустой список, если элементов нет),
/// остальное строкой; пустое — <c>null</c>.
///
/// <para>Отбор, сортировку и страницу исполняет ОНА — запросом к своей базе: общего запроса по
/// произвольным данным ядро не пишет, иначе таблица стала бы вторым обходом изоляции (ТЗ CORE-33).</para>
/// </summary>
public interface IModuleTableRows
{
    Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct);
}
