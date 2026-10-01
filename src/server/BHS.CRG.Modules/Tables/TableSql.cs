using System.Linq.Expressions;

namespace BHS.CRG.Modules.Tables;

/// <summary>
/// Запись числа и даты в клетке поля схемы — те же, что у исполнителя в памяти
/// (<c>TableConditions</c> в Application). Проект контрактов наших проектов не видит, поэтому здесь
/// ЗЕРКАЛО; что оно совпадает с исходным, сверяет тест — так же, как у <see cref="ModuleTableIsolation" />.
/// </summary>
public static class TableSqlRules
{
    public const string NumberPattern = @"^-?[0-9]+(\.[0-9]+)?$";
    public const string DatePattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}";
}

/// <summary>
/// Отбор, сортировка и итоги таблицы запросом к базе модуля — второй исполнитель дерева условий
/// (ТЗ CORE-33; задача G1c, issue #1090). Первый — в памяти, у наборов данных.
///
/// <para><b>Модуль описывает, где лежит каждая колонка</b>; ядро переводит условие в запрос. Так
/// запрос пишет тот, кто знает свою базу, а смысл операторов остаётся один на все модули: «содержит»
/// не различает регистр, «не равно» включает пустые клетки, сравнение числа не трогает клетку с текстом.</para>
///
/// <para><b>Описать надо КАЖДУЮ объявленную колонку</b> — проверяется при сборке
/// (<see cref="Describe" />), то есть на ЛЮБОМ чтении таблицы, а не на первом отборе. Забытая
/// системная колонка иначе ушла бы в поля схемы, отбор по ней искал бы ключ в данных записи, не нашёл
/// бы — и ответил «ничего не найдено», а не отказом; а проверка «при первом отборе» молчала бы, пока
/// таблицу только читают.</para>
///
/// <para>Правило, добавленное сюда и забытое у исполнителя в памяти (или наоборот), роняет парный
/// тест: один отбор на одних данных обязан вернуть одни строки.</para>
/// </summary>
public sealed partial class TableSql<T> where T : class
{
    private readonly ModuleTable declaration;
    private readonly Dictionary<string, Column> _columns = new(StringComparer.Ordinal);
    private Func<string, Expression<Func<T, string?>>>? _field;

    private TableSql(ModuleTable declaration) => this.declaration = declaration;

    /// <summary>
    /// Описать колонки таблицы. Описание проверяется ЗДЕСЬ: каждая объявленная колонка обязана быть
    /// описана, и тем же видом. Негодное описание не даёт прочитать таблицу вовсе — его ловит первый
    /// же тест, читающий таблицу, а не первый пользователь, нажавший «сортировать».
    /// </summary>
    public static TableSql<T> Describe(ModuleTable declaration, Action<TableSql<T>> columns)
    {
        var sql = new TableSql<T>(declaration);
        columns(sql);
        sql.EnsureComplete();
        return sql;
    }

    /// <summary>Текстовая колонка.</summary>
    public TableSql<T> Text(string key, Expression<Func<T, string?>> value) => Add(key, new TextColumn(value));

    /// <summary>Числовая колонка.</summary>
    public TableSql<T> Number(string key, Expression<Func<T, decimal?>> value) =>
        Add(key, new NumberColumn(value, null));

    /// <summary>Колонка-дата.</summary>
    public TableSql<T> Date(string key, Expression<Func<T, DateOnly?>> value) => Add(key, new DateColumn(value));

    /// <summary>Колонка-флаг.</summary>
    public TableSql<T> Flag(string key, Expression<Func<T, bool?>> value) => Add(key, new FlagColumn(value));

    /// <summary>
    /// Колонка-справочник: в базе ссылка (или код), человеку — название. Отбор и сортировка идут по
    /// НАЗВАНИЮ: названия сверяются в памяти правилом ядра, в запрос уходят подошедшие ссылки.
    /// Ссылка, которой в справочнике нет, — пустая клетка, как на экране.
    /// </summary>
    public TableSql<T> Lookup<TKey>(
        string key, Expression<Func<T, TKey?>> id, IReadOnlyDictionary<TKey, string> labels)
        where TKey : struct => Add(key, new LookupColumn<TKey>(id, labels));

    /// <summary>
    /// Поля схемы типа — всё, чего нет среди объявленных колонок. Модуль говорит, как достать текст
    /// поля по ключу; вид поля приходит с условием, и клетка, в которой лежит не то, под сравнение не
    /// попадает.
    /// </summary>
    public TableSql<T> Fields(Func<string, Expression<Func<T, string?>>> field)
    {
        _field = field;
        return this;
    }

    public IQueryable<T> Where(IQueryable<T> rows, TableFilter? filter) =>
        filter is null ? rows : rows.Where(Predicate(filter));

    /// <summary>Сортировка по запросу; null — её не просили, и порядок задаёт модуль.</summary>
    public IOrderedQueryable<T>? OrderBy(IQueryable<T> rows, IReadOnlyList<TableSort>? sort)
    {
        IOrderedQueryable<T>? ordered = null;
        foreach (var by in sort ?? [])
            ordered = Resolve(by.Column, by.Kind).Order(ordered ?? rows, by.Descending, ordered is null);
        return ordered;
    }

    /// <summary>Итоги по колонкам — по всему отбору <paramref name="rows" />, до страницы.</summary>
    public async Task<IReadOnlyDictionary<string, TableTotal>> TotalsAsync(
        IQueryable<T> rows, IReadOnlyDictionary<string, ModuleTableColumnKind>? totals, CancellationToken ct)
    {
        var result = new Dictionary<string, TableTotal>(StringComparer.Ordinal);
        foreach (var (key, kind) in totals ?? new Dictionary<string, ModuleTableColumnKind>())
            result[key] = await Resolve(key, kind).TotalAsync(rows, ct);
        return result;
    }

    private Expression<Func<T, bool>> Predicate(TableFilter filter) => filter switch
    {
        TableFilterCondition condition => Resolve(condition.Column, condition.Kind).Test(condition),
        TableFilterGroup { Children.Count: 0 } => _ => true,
        TableFilterGroup group => group.Children.Select(Predicate).Aggregate((a, b) => Join(a, b, group.Any)),
        _ => throw new InvalidOperationException($"Узла отбора «{filter.GetType().Name}» исполнитель не знает."),
    };

    private TableSql<T> Add(string key, Column column)
    {
        _columns[key] = column;
        return this;
    }

    private Column Resolve(string key, ModuleTableColumnKind kind)
    {
        if (_columns.TryGetValue(key, out var column)) return column;
        if (_field is null)
            throw new InvalidOperationException(
                $"Таблица «{declaration.Code}»: колонки «{key}» нет среди описанных, а полей схемы у таблицы нет.");

        var raw = _field(key);
        return kind switch
        {
            ModuleTableColumnKind.Number => new NumberColumn(Compose(raw, ToNumber), raw),
            ModuleTableColumnKind.Date => new IsoDateColumn(raw),
            _ => new TextColumn(raw),
        };
    }

    private void EnsureComplete()
    {
        var broken = declaration.Columns
            .Where(c => !_columns.TryGetValue(c.Key, out var column) || column.Kind != c.Kind)
            .Select(c => $"«{c.Key}»")
            .ToList();
        if (broken.Count > 0)
            throw new InvalidOperationException(
                $"Таблица «{declaration.Code}»: колонки {string.Join(", ", broken)} объявлены, а запросу не " +
                "описаны (или описаны другим видом). Отбор по такой колонке искал бы её среди полей схемы " +
                "и отвечал бы «ничего не найдено».");
    }
}
