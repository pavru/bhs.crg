namespace BHS.CRG.Application.Tables;

/// <summary>
/// Почему колонка таблицы пришла без значений (ТЗ CORE-33, задача G1b, issue #1089).
///
/// <para><b>Колонка не исчезает — она приходит с причиной.</b> «Скрыта правами», «поле удалено из
/// типа», «модуль выключен» — положительные утверждения об отсутствии, и знает их только сервер.
/// Пришли он меньше колонок, клиент нарисовал бы все три одним дефисом — при зелёных сторожах экрана
/// (ревизия Дизайнера 28.09.2026). Коды совпадают с состояниями общей сетки
/// (<c>shared/ui/dataGridStates.ts</c>).</para>
///
/// <para>Три причины ПОПАРНО различны — и кодом, и текстом; это сторожит тест. Совпади два текста,
/// человек получил бы одну причину на две болезни и искал бы право там, где надо включать модуль.</para>
/// </summary>
public static class TableColumnReasons
{
    /// <summary>Колонку закрывает право, которого у спрашивающего нет.</summary>
    public const string NoRight = "no-right";

    /// <summary>Колонку просили (сохранённое представление, разметка), а поля такого в типе нет.</summary>
    public const string Removed = "removed";

    /// <summary>Модуль, объявивший таблицу, на этом экземпляре выключен.</summary>
    public const string ModuleOff = "module-off";

    /// <summary>«нет права на суммы» — что именно закрыто, называет модуль.</summary>
    public static string NoRightText(string hides) => $"нет права на {hides}";

    public const string RemovedText = "поле удалено из типа";

    public static string ModuleOffText(string moduleTitle) => $"модуль «{moduleTitle}» выключен";
}

/// <summary>
/// Операторы отбора по виду колонки — ОДИН список на двух исполнителей: в памяти
/// (<c>DataSetRowFilterExecutor</c>) и в запросе к базе (G1c). Раздельные списки разошлись бы при
/// добавлении оператора, и разошлись бы в сторону молчания: экран предложил бы условие, которого
/// исполнитель не умеет (ТЗ CORE-33; что исполнители согласны с этим списком, сторожит тест).
///
/// <para>Виды — строками, а не перечислением модулей: Application проекта контрактов модулей не
/// знает, а перевод из объявления модуля стоит в одном месте — в службе таблиц.</para>
/// </summary>
public static class TableOperators
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";

    /// <summary>
    /// Перечень — поле нижнего зерна, свёрнутое в строку верхнего: объекты счёта (G1c, issue #1090).
    /// Операторы те же, что у текста, а спрашивают они «есть ли в перечне такой»; отрицание — «нет ни
    /// одного такого».
    /// </summary>
    public const string List = "list";

    /// <summary>
    /// Выбор из закрытого перечня (G1d, issue #1091): состояние документа, состояние оплаты. Операторы
    /// — равенство и вхождение в список; значение вне перечня колонки делает условие негодным
    /// (<see cref="TableConditions.Problem" />), а не «ничего не нашедшим».
    /// </summary>
    public const string Choice = "choice";

    private static readonly string[] Presence = ["is_empty", "is_not_empty"];
    private static readonly string[] Undefined = ["is_null", "is_not_null"];
    private static readonly string[] Equality = ["eq", "neq"];
    private static readonly string[] Order = ["gt", "gte", "lt", "lte"];
    private static readonly string[] Membership = ["in", "not_in"];

    /// <summary>
    /// Вид → операторы, в порядке, в каком их предлагать.
    ///
    /// <para>У типизированных видов «пусто» называется «не определён» (<c>is_null</c>): у даты и числа
    /// пустой строки не бывает, бывает отсутствие значения — «счёт без срока» (G1c, issue #1090).
    /// Исполнители при этом принимают оба имени у любого вида (<see cref="IsPresence" />): отборы,
    /// сохранённые до G1c, спрашивали <c>is_empty</c> у числа, и менять им смысл нельзя.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByKind =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Text] = [.. Equality, "contains", "not_contains", "starts_with", "ends_with", .. Membership, .. Presence],
            [Number] = [.. Equality, .. Order, "between", .. Membership, .. Undefined],
            [Date] = [.. Equality, .. Order, "between", .. Membership, .. Undefined],
            [Boolean] = [.. Equality, .. Undefined],
            [List] = [.. Equality, "contains", "not_contains", "starts_with", "ends_with", .. Membership, .. Presence],
            [Choice] = [.. Equality, .. Membership],
        };

    /// <summary>Все операторы, какие бывают.</summary>
    public static IReadOnlySet<string> All { get; } =
        ByKind.Values.SelectMany(o => o).ToHashSet(StringComparer.Ordinal);

    /// <summary>«Значения нет» — под обоими именами, у любого вида.</summary>
    public static bool IsPresence(string op) => op is "is_empty" or "is_not_empty" or "is_null" or "is_not_null";

    /// <summary>Сколько значений несёт условие: 0 — ни одного, 1, 2 (границы), -1 — список из одного и более.</summary>
    public static int Arity(string op) => op switch
    {
        _ when IsPresence(op) => 0,
        "between" => 2,
        "in" or "not_in" => -1,
        _ => 1,
    };

    /// <summary>
    /// Что не так с числом значений условия; null — годно. ОДНА проверка на оба режима исполнителя
    /// (по догадке и по виду) и на экран таблицы: у каждого своя разошлась бы текстом и строгостью —
    /// «равно» с двумя значениями один режим отверг бы, а другой молча сравнил бы с первым.
    /// </summary>
    public static string? ArityProblem(string op, IReadOnlyList<string?> values)
    {
        if (values.Any(v => v is null)) return $"среди значений оператора «{op}» есть пустое место (null)";

        return Arity(op) switch
        {
            0 when values.Count != 0 => $"оператор «{op}» значения не принимает, а их задано {values.Count}",
            1 when values.Count != 1 => $"оператору «{op}» нужно одно значение, а задано {values.Count}",
            2 when values.Count != 2 => $"оператору «{op}» нужны две границы, а значений задано {values.Count}",
            < 0 when values.Count == 0 => $"оператору «{op}» нужен список значений, а он пуст",
            _ => null,
        };
    }

    public static IReadOnlyList<string> For(string kind) =>
        ByKind.TryGetValue(kind, out var ops)
            ? ops
            : throw new InvalidOperationException($"Вида колонки «{kind}» нет: операторов для него не объявлено.");
}

/// <summary>Колонка таблицы так, как её получает потребитель — экран или набор данных.</summary>
/// <param name="Key">Ключ значения в строке.</param>
/// <param name="Label">Заголовок.</param>
/// <param name="Kind">Вид значения — см. <see cref="TableOperators" />.</param>
/// <param name="Operators">Допустимые операторы отбора.</param>
/// <param name="System">Системная колонка модуля (true) или поле схемы типа (false).</param>
/// <param name="Unavailable">Код причины из <see cref="TableColumnReasons" />; null — колонка открыта.</param>
/// <param name="Reason">Та же причина словами: «нет права на суммы».</param>
/// <param name="DependsOnFilter">Значение колонки зависит от самого отбора (ТЗ CORE-33): сумма под
/// отбором по стройке — доля по разноске. По такой колонке не отбирают и не сортируют — операторов у
/// неё нет, — и в набор данных она не едет.</param>
/// <param name="Note">Что колонка значит ПОД ЭТИМ ОТБОРОМ — подписью к заголовку: «доля: Комарова
/// 36». null — смысл обычный.</param>
/// <param name="Options">Значения закрытого перечня — у колонки вида «выбор» (G1d, issue #1091):
/// экран предлагает их списком, и по ним же проверяется условие. null — перечня у колонки нет.</param>
/// <param name="Requires">Код права, которого не хватило, — у колонки с причиной
/// <see cref="TableColumnReasons.NoRight" /> (G1e, issue #1092). Слова причины человек понесёт
/// администратору, а тот ищет право по коду: совпадение строки избавляет обоих от угадывания.</param>
public record TableColumnDto(
    string Key, string Label, string Kind, IReadOnlyList<string> Operators, bool System,
    string? Unavailable = null, string? Reason = null, bool DependsOnFilter = false, string? Note = null,
    IReadOnlyList<string>? Options = null, string? Requires = null);

/// <summary>
/// Таблица без строк — что она такое и из чего состоит (G1e, issue #1092). Экран спрашивает это
/// отдельно от строк: выбор колонок и чипы отбора обязаны знать ВСЕ колонки, а не показанные, и
/// обязаны уцелеть, когда отбор отказал, — отказ строк приходит без единой колонки, и исправить
/// негодное условие было бы нечем.
/// </summary>
/// <param name="Columns">Все колонки таблицы: объявленные модулем и поля схемы типа; закрытые — с
/// причиной.</param>
/// <param name="State">Состояние таблицы целиком: <see cref="TableColumnReasons.ModuleOff" /> или null.</param>
/// <param name="Views">Готовые представления таблицы, которые поставляет модуль (G4, issue #1097):
/// «Реестр счетов». Пусто — готовых нет. У таблицы выключенного модуля — тоже пусто: настраивать
/// нечего, строк нет.</param>
public record TableDeclarationDto(
    string Address, string Title, string Grain, string Boundary,
    IReadOnlyList<TableColumnDto> Columns, string? State = null,
    IReadOnlyList<TableViewDto>? Views = null);

/// <summary>
/// Готовое представление таблицы — её настройка от модуля (ТЗ CORE-33, COST-20.1; задача G4,
/// issue #1097): состав и порядок колонок, сортировка, итоги, закрепление и колонки, по которым
/// экран предлагает отбор. Условий отбора в нём нет — их ставит человек.
/// </summary>
/// <param name="Code">Код представления — им оно названо в адресе экрана.</param>
/// <param name="Filters">Колонки, по которым отбор предлагается готовыми местами под условие.</param>
public record TableViewDto(
    string Code, string Title, IReadOnlyList<string> Columns,
    IReadOnlyList<TableSortRequest> Sort, IReadOnlyList<TableViewTotalDto> Totals, int Pinned,
    IReadOnlyList<string> Filters);

/// <summary>Итог готового представления: колонка и слово итога (<c>sum</c>, <c>count</c>…).</summary>
public record TableViewTotalDto(string Column, string Aggregate);

/// <summary>Таблица модуля со строками.</summary>
/// <param name="Address">Адрес таблицы: <c>costs.invoices</c>.</param>
/// <param name="State">Состояние таблицы целиком: <see cref="TableColumnReasons.ModuleOff" /> или null.</param>
/// <param name="Rows">Строки страницы. Значений закрытых колонок в них нет вовсе — ключа нет, а не null.</param>
/// <param name="Count">Сколько строк в отборе ВСЕГО, а не на странице.</param>
/// <param name="Offset">С какой строки отбора начинается страница.</param>
/// <param name="Limit">Размер страницы; null — отданы все строки отбора.</param>
/// <param name="Totals">Итоги по запрошенным колонкам — по всему отбору, а не по странице.</param>
/// <param name="Keys">Ключи строк — по одному на строку, в том же порядке; ими экран называет строку,
/// открытую в боковой панели (<see cref="TableRequest.Row" />). null — таблица ключей не называет.</param>
public record TableDto(
    string Address, string Title, string Grain, string Boundary,
    IReadOnlyList<TableColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? State = null,
    int Count = 0, int Offset = 0, int? Limit = null,
    IReadOnlyDictionary<string, TableTotalDto>? Totals = null,
    IReadOnlyList<string>? Keys = null);

/// <summary>
/// Итог по колонке (ТЗ CORE-33): у числа сумма, среднее, минимум и максимум; у даты минимум и
/// максимум; у остального количество.
/// </summary>
/// <param name="Count">Сколько значений учтено.</param>
/// <param name="Skipped">Сколько значений НЕ учтено: в клетке лежит не то, что обещает вид колонки.</param>
/// <param name="SkippedReason">Почему не учтены: «не число», «не дата». null — учтены все.</param>
/// <param name="Note">Что итог значит под этим отбором — подписью под ним; null — оговорки нет.</param>
public record TableTotalDto(
    long Count, long Skipped, string? SkippedReason,
    decimal? Sum = null, decimal? Average = null, object? Min = null, object? Max = null, string? Note = null);

/// <summary>Что потребитель просит у таблицы.</summary>
/// <param name="Columns">Колонки представления; null — все объявленные.</param>
/// <param name="Filter">Отбор — дерево условий в том же виде, что у источника набора данных.</param>
/// <param name="Sort">Сортировка по порядку важности.</param>
/// <param name="Limit">Размер страницы; null — все строки (так таблицу читает набор данных).</param>
/// <param name="Totals">Колонки, по которым нужен итог.</param>
/// <param name="Row">Ключ одной строки: отдать только её, и только если она в отборе.</param>
public record TableRequest(
    IReadOnlyList<string>? Columns = null,
    string? Filter = null,
    IReadOnlyList<TableSortRequest>? Sort = null,
    int Offset = 0, int? Limit = null,
    IReadOnlyList<string>? Totals = null,
    string? Row = null);

public record TableSortRequest(string Column, bool Descending);

/// <summary>Таблица в перечне — без строк.</summary>
public record TableListItemDto(string Address, string Title, string Grain, string Module);
