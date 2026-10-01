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

    private static readonly string[] Presence = ["is_empty", "is_not_empty"];
    private static readonly string[] Equality = ["eq", "neq"];
    private static readonly string[] Order = ["gt", "gte", "lt", "lte"];

    /// <summary>Вид → допустимые операторы, в порядке, в каком их предлагать.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByKind =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Text] = [.. Equality, "contains", "not_contains", "starts_with", "ends_with", .. Presence],
            [Number] = [.. Equality, .. Order, .. Presence],
            [Date] = [.. Equality, .. Order, .. Presence],
            [Boolean] = [.. Equality, .. Presence],
        };

    /// <summary>Все операторы, какие бывают.</summary>
    public static IReadOnlySet<string> All { get; } =
        ByKind.Values.SelectMany(o => o).ToHashSet(StringComparer.Ordinal);

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
public record TableColumnDto(
    string Key, string Label, string Kind, IReadOnlyList<string> Operators, bool System,
    string? Unavailable = null, string? Reason = null);

/// <summary>Таблица модуля со строками.</summary>
/// <param name="Address">Адрес таблицы: <c>costs.invoices</c>.</param>
/// <param name="State">Состояние таблицы целиком: <see cref="TableColumnReasons.ModuleOff" /> или null.</param>
/// <param name="Rows">Строки. Значений закрытых колонок в них нет вовсе — ключа нет, а не null.</param>
public record TableDto(
    string Address, string Title, string Grain, string Boundary,
    IReadOnlyList<TableColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? State = null);

/// <summary>Таблица в перечне — без строк.</summary>
public record TableListItemDto(string Address, string Title, string Grain, string Module);
