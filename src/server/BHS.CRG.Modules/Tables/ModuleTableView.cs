namespace BHS.CRG.Modules.Tables;

/// <summary>Сортировка готового представления.</summary>
public sealed record ModuleTableViewSort(string Column, bool Descending = false);

/// <summary>Итог готового представления: под какой колонкой и какой.</summary>
/// <param name="Aggregate">Слово из <see cref="ModuleTableView.Aggregates" />: <c>sum</c>, <c>avg</c>,
/// <c>min</c>, <c>max</c>, <c>count</c>.</param>
public sealed record ModuleTableViewTotal(string Column, string Aggregate);

/// <summary>
/// Готовое представление таблицы — её настройка, которую поставляет модуль (ТЗ CORE-33: «модули
/// поставляют готовые представления, пользователь делает свои»; COST-20.1; задача G4, issue #1097).
/// «Реестр счетов» — не отдельный отчёт, а таблица счетов с названным составом колонок.
///
/// <para><b>Объявляется рядом с таблицей, а не на экране.</b> Состав колонок представления — такое же
/// обещание, как состав самой таблицы: колонку переименовали или убрали — и представление, живущее в
/// клиенте, открылось бы с дырой, о которой сервер не знает. Здесь негодное представление останавливает
/// старт и называет себя, как негодная таблица (<see cref="ModuleTableCatalog" />).</para>
///
/// <para><b>Это настройка, а не отбор строк.</b> Представление не сужает то, что человеку видно, и
/// прав не добавляет: колонка, закрытая правом, под ним закрыта так же. Поэтому условий отбора у него
/// нет — только <see cref="Filters" />: по каким колонкам отбор ПРЕДЛАГАЕТСЯ. Зашитое условие было бы
/// отбором, который человек не ставил и снять не может.</para>
/// </summary>
/// <param name="Code">Код представления внутри таблицы: <c>registry</c>. Им оно названо в адресе экрана.</param>
/// <param name="Title">Название для человека: «Реестр счетов».</param>
/// <param name="Columns">Колонки в порядке показа — ключи объявленных колонок таблицы.</param>
/// <param name="Sort">Сортировка; пусто — порядок таблицы по умолчанию.</param>
/// <param name="Totals">Итоги под колонками. Считаются по всему отбору, как любые итоги таблицы.</param>
/// <param name="Pinned">Сколько первых колонок закреплено слева.</param>
/// <param name="Filters">Колонки, по которым экран предлагает отбор готовыми местами под условие:
/// период, плательщик, поставщик. Условие ставит человек.</param>
public sealed record ModuleTableView(
    string Code,
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ModuleTableViewSort>? Sort = null,
    IReadOnlyList<ModuleTableViewTotal>? Totals = null,
    int Pinned = 0,
    IReadOnlyList<string>? Filters = null)
{
    /// <summary>Итоги, которые умеет таблица, — те же слова, что в адресе экрана.</summary>
    public static readonly IReadOnlyList<string> Aggregates = ["sum", "avg", "min", "max", "count"];

    /// <summary>
    /// Какие итоги бывают у колонки (ТЗ CORE-33): у числа все, у даты — края и количество, у остального
    /// — количество. То же правило держит экран; разойдись они, представление открылось бы с итогом,
    /// который экран назвал бы несчитаемым.
    /// </summary>
    public static IReadOnlyList<string> AggregatesFor(ModuleTableColumnKind kind) => kind switch
    {
        ModuleTableColumnKind.Number => Aggregates,
        ModuleTableColumnKind.Date => ["min", "max", "count"],
        _ => ["count"],
    };

    /// <summary>Что не так с представлением; пусто — годно. Собирает ВСЁ, а не первое.</summary>
    /// <param name="declared">Объявленные колонки таблицы. Поля схемы типа сюда не входят: их состав
    /// меняет заказчик, и представление модуля, опирающееся на них, ломалось бы правкой типа.</param>
    public IReadOnlyList<string> Problems(IReadOnlyList<ModuleTableColumn> declared)
    {
        var problems = new List<string>();
        var name = string.IsNullOrWhiteSpace(Code) ? "без кода" : $"«{Code}»";
        void Add(string problem) => problems.Add($"представление {name}: {problem}");

        if (string.IsNullOrWhiteSpace(Code)) Add("не назван код");
        if (string.IsNullOrWhiteSpace(Title)) Add("нет названия для человека");

        // null — негодное объявление, а не исключение без адреса таблицы и кода представления: модуль
        // может собираться без проверки nullable (ревью PR #1130, #1177). Колонку без ключа называет
        // сама таблица; здесь она просто не участвует — иначе проверка упала бы, не назвав остального.
        var byKey = (declared ?? []).Where(c => c?.Key is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var columns = Named(Columns, "в списке колонок");
        if (columns.Count == 0) Add("не названо ни одной колонки");

        foreach (var key in columns.Where(k => !byKey.ContainsKey(k)).Distinct(StringComparer.Ordinal))
            Add($"колонки «{key}» в таблице нет");
        foreach (var twice in columns.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add($"колонка «{twice.Key}» названа дважды");

        if (Pinned < 0 || Pinned > columns.Count)
            Add($"закреплено колонок — {Pinned}, а в представлении их {columns.Count}");

        foreach (var sort in Sort ?? [])
        {
            if (sort?.Column is null) { Add("в сортировке пустое место"); continue; }
            if (!byKey.TryGetValue(sort.Column, out var column)) Add($"сортировка по колонке «{sort.Column}», которой в таблице нет");
            // По колонке, чей смысл зависит от отбора, ядро не сортирует — запрос отказал бы на первом
            // же открытии представления.
            else if (column.DependsOnFilter) Add($"сортировка по колонке «{sort.Column}», а её значение зависит от отбора");
            // Сортировке по закрытой колонке ядро отвечает отказом, а не пропуском: тот, у кого права
            // нет, не открыл бы представление ВОВСЕ — а оно правами не сужается. Итог по закрытой
            // колонке ядро молча не считает, колонку показывает причиной; сортировку — нет.
            else if (!string.IsNullOrWhiteSpace(column.Requires))
                Add($"сортировка по колонке «{sort.Column}», а она закрыта правом «{column.Requires}»: " +
                    "тому, у кого права нет, представление не открылось бы вовсе");
        }

        var totals = (Totals ?? []).Where(t => t?.Column is not null).ToList();
        if (totals.Count != (Totals ?? []).Count) Add("в итогах пустое место");
        foreach (var total in totals)
        {
            if (!columns.Contains(total.Column, StringComparer.Ordinal))
                Add($"итог под колонкой «{total.Column}», которой в представлении нет");
            else if (byKey.TryGetValue(total.Column, out var column)
                     && !AggregatesFor(column.Kind).Contains(total.Aggregate, StringComparer.Ordinal))
                Add($"итог «{total.Aggregate}» под колонкой «{total.Column}» не считается: у её вида бывают " +
                    string.Join(", ", AggregatesFor(column.Kind)));
        }
        foreach (var twice in totals.GroupBy(t => t.Column, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add($"под колонкой «{twice.Key}» два итога");

        var filters = Named(Filters, "в местах под отбор");
        foreach (var key in filters)
        {
            if (!byKey.TryGetValue(key, out var column)) Add($"отбор предложен по колонке «{key}», которой в таблице нет");
            else if (column.DependsOnFilter) Add($"отбор предложен по колонке «{key}», а по ней не отбирают");
        }
        foreach (var twice in filters.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add($"отбор по колонке «{twice.Key}» предложен дважды");

        return problems;

        // Ключи списка без пустых мест; пустое место названо один раз.
        List<string> Named(IReadOnlyList<string>? keys, string where)
        {
            var named = (keys ?? []).Where(k => k is not null).ToList();
            if (named.Count != (keys ?? []).Count) Add($"{where} пустое место");
            return named;
        }
    }
}
