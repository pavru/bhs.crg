namespace BHS.CRG.Modules.Tables;

/// <summary>
/// Расшифровка строки — её нижнее зерно под ТЕМ ЖЕ отбором (ТЗ CORE-33, COST-20.1; задача G4, issue
/// #1097): счёт, разнесённый на несколько строек, «раскрывается по ним» в боковой панели строки.
///
/// <para><b>Объявление — в таблице, данные — в странице.</b> Заголовок и колонки не зависят от строки
/// и живут в <see cref="ModuleTable.Breakdown" />: негодная расшифровка останавливает старт и называет
/// себя, как негодная таблица, а не всплывает на первом открытии панели (ревизия Архитектора). Строки
/// приходят со страницей (<see cref="ModuleTablePage.Breakdown" />) — в ответе того же чтения одной
/// строки: одно «сегодня», один отбор, один разбор условий. Отдельный запрос читал бы запись второй
/// раз, и между чтениями число «в отборе» расходилось бы с клеткой.</para>
///
/// <para><b>Это не группировка сетки.</b> Подстроки не сортируются, не входят в счёт строк и в итог;
/// группы с промежуточными числами ТЗ откладывает.</para>
/// </summary>
/// <param name="Title">Заголовок блока: «Разноска».</param>
/// <param name="Columns">Колонки расшифровки в порядке показа.</param>
/// <param name="Sums">Какие числовые колонки расшифровки складываются в какие колонки таблицы.</param>
public sealed record ModuleTableBreakdown(
    string Title,
    IReadOnlyList<TableBreakdownColumn> Columns,
    IReadOnlyList<TableBreakdownSum>? Sums = null)
{
    /// <summary>Что не так с объявлением; пусто — годно. Собирает ВСЁ, а не первое.</summary>
    /// <param name="declared">Объявленные колонки таблицы; поля схемы типа сюда не входят — как у
    /// представлений: их состав меняет заказчик.</param>
    public IReadOnlyList<string> Problems(IReadOnlyList<ModuleTableColumn> declared)
    {
        var problems = new List<string>();
        void Add(string problem) => problems.Add($"расшифровка строки: {problem}");

        var byKey = (declared ?? []).Where(c => c?.Key is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(Title)) Add("нет заголовка");
        var columns = (Columns ?? []).Where(c => c is not null).ToList();
        if (columns.Count == 0) Add("не объявлено ни одной колонки");
        if ((Columns ?? []).Any(c => c is null)) Add("в списке колонок пустое место");

        foreach (var twice in columns.GroupBy(c => c.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add($"колонка «{twice.Key}» объявлена дважды");

        foreach (var column in columns)
        {
            if (string.IsNullOrWhiteSpace(column.Key) || string.IsNullOrWhiteSpace(column.Title))
                Add("у колонки нет ключа или заголовка");
            if (column.Kind is not (ModuleTableColumnKind.Text or ModuleTableColumnKind.Number or ModuleTableColumnKind.Date))
                Add($"у колонки «{column.Key}» вид не текст, не число и не дата");

            if (column.Follows is { } follows && !byKey.ContainsKey(follows))
                Add($"колонка «{column.Key}» следует за колонкой «{follows}», которой в таблице нет");
            // Молчание о доступе у числа — отказ, а не «открыто всем»: забытая привязка у «Доли» была бы
            // тихой утечкой сумм. Число без денег говорит это явно — следуя за открытой колонкой.
            if (column.Kind == ModuleTableColumnKind.Number && column.Follows is null)
                Add($"у числовой колонки «{column.Key}» не названо, за какой колонкой таблицы она следует " +
                    "в доступе: без этого число ушло бы и тому, кому закрыты суммы");
        }

        var own = columns.Where(c => c.Key is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var sum in Sums ?? [])
        {
            if (sum is null) { Add("в списке сумм пустое место"); continue; }
            if (!own.TryGetValue(sum.Column ?? "", out var column)) Add($"сумма по колонке «{sum.Column}», которой в расшифровке нет");
            else if (column.Kind != ModuleTableColumnKind.Number) Add($"сумма по колонке «{sum.Column}», а она не число");

            foreach (var target in new[] { sum.Named, sum.Whole }.Where(t => t is not null))
            {
                if (!byKey.TryGetValue(target!, out var cell)) Add($"сумма колонки «{sum.Column}» сверяется с колонкой «{target}», которой в таблице нет");
                else if (cell.Kind != ModuleTableColumnKind.Number) Add($"сумма колонки «{sum.Column}» сверяется с колонкой «{target}», а она не число");
            }
        }
        foreach (var twice in (Sums ?? []).Where(s => s?.Column is not null)
                     .GroupBy(s => s.Column, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add($"сумма по колонке «{twice.Key}» объявлена дважды");

        return problems;
    }
}

/// <summary>Колонка расшифровки.</summary>
/// <param name="Key">Ключ значения в строке расшифровки.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Kind">Текст, число или дата — перечню и выбору в расшифровке делать нечего.</param>
/// <param name="Follows">
/// Ключ колонки ТАБЛИЦЫ, чьим доступом закрыта эта: «Доля» следует за «Суммой». Закрыта та — ядро
/// вычищает значения и называет ту же причину. Своих прав расшифровка не объявляет: второе описание
/// «что закрыто суммами» разошлось бы с первым. null — открыта всем, кому открыта таблица; у числа
/// так нельзя (см. <see cref="ModuleTableBreakdown.Problems" />).
/// </param>
public sealed record TableBreakdownColumn(string Key, string Title, ModuleTableColumnKind Kind, string? Follows = null);

/// <summary>
/// Во что складывается числовая колонка расшифровки. Суммы считает ЯДРО из строк, а не модуль: число,
/// присланное готовым, нельзя было бы ни проверить, ни вычистить вместе с колонкой. И сверяет их с
/// клеткой строки — расшифровка, не сходящаяся со своей строкой, отказ, а не две цифры на экране.
/// </summary>
/// <param name="Column">Числовая колонка расшифровки: «Доля».</param>
/// <param name="Named">Колонка таблицы, которой равна сумма НАЗВАННЫХ отбором строк расшифровки, а без
/// сужающего отбора — всех: «Сумма» (она и сама зависит от отбора).</param>
/// <param name="Whole">Колонка таблицы, которой равна сумма ВСЕХ строк расшифровки: «Сумма к оплате».
/// null — сверять не с чем.</param>
public sealed record TableBreakdownSum(string Column, string Named, string? Whole = null);

/// <summary>Расшифровка одной строки — данные (см. <see cref="ModuleTableBreakdown" />).</summary>
/// <param name="Rows">Строки расшифровки в порядке показа.</param>
/// <param name="Narrowed">Отбор сужает строку до части: тогда у строк расшифровки есть смысл «в отборе».
/// Отдельным признаком, а не «названы все»: «называть нечем» и «названо всё» — разные ответы.</param>
/// <param name="Note">Оговорка под заголовком: «счёт не оплачен — в затраты не вошёл». Свободный
/// текст модуля ядро вычистить не может — поэтому не отдаёт его вовсе, если хоть одна колонка
/// расшифровки человеку закрыта.</param>
public sealed record TableRowBreakdown(
    IReadOnlyList<TableBreakdownRow> Rows, bool Narrowed, string? Note = null);

/// <summary>Строка расшифровки.</summary>
/// <param name="Values">Значения по ключам объявленных колонок; незнакомый ключ — ошибка модуля.</param>
/// <param name="Named">Эта часть названа отбором («в отборе»); вне сужающего отбора не читается.</param>
public sealed record TableBreakdownRow(IReadOnlyDictionary<string, object?> Values, bool Named = false);
