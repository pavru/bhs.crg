namespace BHS.CRG.Modules.Tables;

/// <summary>
/// Вид значения колонки таблицы (ТЗ CORE-33, задача G1b этапа 2, issue #1089).
///
/// <para>Колонки ТИПИЗИРОВАНЫ, а не «всё строками», как у наборов данных: от вида зависят и
/// допустимые операторы отбора, и итог. Сумма у числа, минимум и максимум у даты, количество у
/// остального — а угадывать вид по значениям значит считать «12 шт» числом (#1064).</para>
/// </summary>
public enum ModuleTableColumnKind
{
    Text,
    Number,
    Date,
    Boolean,

    /// <summary>
    /// Перечень — поле НИЖНЕГО зерна, свёрнутое в строку верхнего (ТЗ CORE-33: «поле нижнего уровня в
    /// верхнем зерне — только свёрнутым»): объекты, на которые разнесён счёт. Значение в строке —
    /// список названий.
    ///
    /// <para>Условие по такой колонке — условие ПО ДОЧЕРНЕМУ ЗЕРНУ (G1c, issue #1090): «есть часть, у
    /// которой…». «Объект равен X» находит счёт, у которого хоть одна часть легла на X; отрицание —
    /// счёт, у которого такой части нет ни одной.</para>
    /// </summary>
    List,

    /// <summary>
    /// Выбор из ЗАКРЫТОГО перечня значений (G1d, issue #1091): состояние документа, состояние оплаты.
    /// Значения перечня называет объявление колонки (<see cref="ModuleTableColumn.Options" />).
    ///
    /// <para>Отдельный вид, а не текст со списком подсказок, потому что у него другое обещание: условие
    /// со значением вне перечня — ОТКАЗ. У текста «Состояние равно Оплочен» молча ничего не находит, и
    /// опечатка выглядит как «таких счетов нет».</para>
    /// </summary>
    Choice,
}

/// <summary>
/// Насколько узко таблица отбирает строки (ТЗ CORE-24.1).
///
/// ⚠️ Это ЗЕРКАЛО <c>SystemDataSetIsolation</c> из Application — по той же причине, что
/// <see cref="ModuleSchemaLevel" />: ссылок на наши проекты в этом проекте нет вовсе. Сторож
/// (<c>ModuleTableCatalogTests</c>) сверяет состав значений по именам.
///
/// <para><see cref="Unset" /> — умолчание, и оно НЕГОДНО: иначе забытое объявление совпало бы с
/// <see cref="None" />, то есть самое широкое поведение достигалось бы молчанием.</para>
/// </summary>
public enum ModuleTableIsolation
{
    Unset = 0,

    /// <summary>Кому открыта таблица, тот видит все строки.</summary>
    None,

    /// <summary>Строки отбираются по правам спрашивающего.</summary>
    PerUser,
}

/// <summary>
/// Колонка таблицы, которую объявляет модуль: системная — её значение кладёт код модуля.
/// </summary>
/// <param name="Key">Ключ колонки. Совпадает с ключом поля типа, если колонка за ним стоит: тогда поле
/// схемы второй раз не предлагается.</param>
/// <param name="Title">Заголовок для человека.</param>
/// <param name="Kind">Вид значения — от него зависят операторы отбора и итог.</param>
/// <param name="Requires">Ключ доступа, без которого колонка приходит пустой и с причиной, — право
/// или код модуля. null — колонке хватает ключа таблицы.
///
/// <para>⚠️ Колонка без права НЕ исчезает из ответа (ТЗ CORE-33): исчезнувшая читается как поломка, а
/// три разных отсутствия — «нет права», «поля нет в типе», «модуль выключен» — на экране стали бы
/// одним дефисом.</para></param>
/// <param name="Hides">Что именно закрыто — словами модуля, для причины «нет права на …»: «суммы».
/// Обязано стоять вместе с <paramref name="Requires" />: ядро на его месте сочинило бы безликое «нет
/// права», и человек не узнал бы, какого права ему не хватает.</param>
/// <param name="DependsOnFilter">Значение колонки зависит от САМОГО ОТБОРА (ТЗ CORE-33: «отбор по
/// стройке меняет смысл суммы, и интерфейс это говорит»): сумма счёта под отбором по стройке — доля по
/// разноске. Что колонка значит под данным отбором, служба строк говорит подписью
/// (<see cref="ModuleTablePage.Notes" />).
///
/// <para>⚠️ По такой колонке ядро не отбирает и не сортирует, и в набор данных она не едет. Условие
/// «сумма больше миллиона» меняло бы смысл вместе с соседним условием — молча; а набор отбирает строки
/// сам, уже после чтения, и доля в нём посчиталась бы без отбора: полной суммой под именем доли.</para></param>
/// <param name="Options">Значения закрытого перечня — у колонки вида <see cref="ModuleTableColumnKind.Choice" />
/// и только у неё, в порядке показа. Те же слова, что стоят в клетках: по ним идёт отбор, и их же
/// предлагает экран.</param>
public sealed record ModuleTableColumn(
    string Key,
    string Title,
    ModuleTableColumnKind Kind,
    string? Requires = null,
    string? Hides = null,
    bool DependsOnFilter = false,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// Табличный источник, который объявляет модуль (ТЗ CORE-33, CORE-24; задача G1b, issue #1089).
///
/// <para><b>Одно объявление — два потребителя</b>: экран (табличное представление) и набор данных.
/// Иначе появились бы две таблицы «Счета» с разным составом колонок, и каждая была бы права по-своему.
/// Ядро общего запроса по произвольным данным НЕ пишет: строки отбирает служба модуля
/// (<see cref="Reader" />), иначе таблица стала бы вторым обходом изоляции.</para>
///
/// <para><b>Параметр доступа обязателен.</b> Таблица без него не регистрируется — приложение не
/// стартует и называет таблицу (<see cref="ModuleTableCatalog" />). Отказ на первом чтении пришёл бы
/// пользователю пятисотым ответом, а приложение считалось бы поднявшимся.</para>
/// </summary>
/// <param name="Code">Код таблицы внутри модуля: <c>invoices</c>. Полный адрес — <c>costs.invoices</c>.</param>
/// <param name="Title">Название для человека: «Счета на оплату».</param>
/// <param name="Grain">Зерно — что считается строкой, словами модуля: «счёт», «строка счёта», «часть
/// разноски». Зерно не меняется: поле нижнего уровня в верхнем зерне бывает только свёрнутым.</param>
/// <param name="Requires">Ключ доступа к таблице — право или код модуля. Обязателен.</param>
/// <param name="Isolation">Вид отбора строк — обязан быть выбран, см. <see cref="ModuleTableIsolation" />.</param>
/// <param name="Boundary">Текст границы выдачи (ТЗ CORE-24.3) — словами модуля, что именно отдаёт
/// таблица и кому.</param>
/// <param name="Columns">Системные колонки в порядке показа.</param>
/// <param name="Reader">Служба модуля, отбирающая строки: реализует <see cref="IModuleTableRows" /> и
/// зарегистрирована модулем в <c>RegisterServices</c>. Объявленная таблица без зарегистрированной
/// службы — отказ при старте, как схема без контекста.</param>
/// <param name="RecordType">Код типа записи, чьи поля схемы тоже доступны колонками (поля, которые
/// заказчик дописал в тип). null — таблица живёт одними системными колонками.</param>
/// <param name="Views">Готовые представления таблицы — её настройки, которые поставляет модуль
/// (<see cref="ModuleTableView" />): «Реестр счетов» у таблицы счетов. null — готовых нет, и таблица
/// открывается всеми колонками в порядке объявления.</param>
public sealed record ModuleTable(
    string Code,
    string Title,
    string Grain,
    string Requires,
    ModuleTableIsolation Isolation,
    string Boundary,
    IReadOnlyList<ModuleTableColumn> Columns,
    Type Reader,
    string? RecordType = null,
    IReadOnlyList<ModuleTableView>? Views = null)
{
    /// <summary>Полный адрес таблицы: <c>модуль.таблица</c>.</summary>
    public static string Address(string module, string code) => $"{module}.{code}";

    /// <summary>Что не так с объявлением; null — годно. Собирает ВСЁ, а не первое.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Code)) problems.Add("не назван код таблицы");
        if (string.IsNullOrWhiteSpace(Title)) problems.Add("нет названия для человека");
        if (string.IsNullOrWhiteSpace(Grain)) problems.Add("не названо зерно — что считается строкой");
        if (string.IsNullOrWhiteSpace(Requires))
            problems.Add("не назван параметр доступа (право или код модуля): без него таблица отдавала бы " +
                         "строки всем, кто вошёл");
        if (Isolation == ModuleTableIsolation.Unset) problems.Add("не объявлен вид отбора строк");
        if (string.IsNullOrWhiteSpace(Boundary)) problems.Add("нет текста границы выдачи");
        // null у ссылочных полей — тоже негодное объявление, а не NullReferenceException без имени
        // таблицы: модуль может собираться без проверки nullable (ревью PR #1130).
        if (Reader is null) problems.Add("не названа служба строк");
        else if (!typeof(IModuleTableRows).IsAssignableFrom(Reader))
            problems.Add($"служба строк «{Reader.Name}» не реализует {nameof(IModuleTableRows)}");
        if (Columns is null)
        {
            problems.Add("не объявлено ни одной колонки");
            return problems;
        }
        if (Columns.Count == 0) problems.Add("не объявлено ни одной колонки");

        foreach (var key in Columns.Where(c => c is not null).GroupBy(c => c.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"колонка «{key.Key}» объявлена дважды");

        foreach (var column in Columns)
        {
            if (column is null) { problems.Add("в списке колонок пустое место"); continue; }
            if (string.IsNullOrWhiteSpace(column.Key) || string.IsNullOrWhiteSpace(column.Title))
                problems.Add("у колонки нет ключа или заголовка");
            if (!Enum.IsDefined(column.Kind))
                problems.Add($"у колонки «{column.Key}» неизвестный вид значения");
            if (string.IsNullOrWhiteSpace(column.Requires) != string.IsNullOrWhiteSpace(column.Hides))
                problems.Add($"у колонки «{column.Key}» право и то, что оно закрывает, названы не вместе: " +
                             "без второго отказ сказал бы «нет права» и не сказал бы, на что");

            // Перечень и вид «выбор» — только вместе. Выбор без перечня отвергал бы любое значение, а
            // перечень у текста никто бы не проверял: экран предложил бы список, а отбор принял бы и
            // то, чего в нём нет.
            var options = column.Options ?? [];
            if (column.Kind == ModuleTableColumnKind.Choice && options.Count == 0)
                problems.Add($"у колонки-выбора «{column.Key}» не назван перечень значений");
            if (column.Kind != ModuleTableColumnKind.Choice && column.Options is not null)
                problems.Add($"у колонки «{column.Key}» назван перечень значений, а вид у неё — не «выбор»");
            if (options.Any(string.IsNullOrWhiteSpace))
                problems.Add($"в перечне колонки «{column.Key}» есть пустое значение");
            if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
                problems.Add($"в перечне колонки «{column.Key}» значение названо дважды");
        }

        // Представления — после колонок: они на колонки ссылаются, и негодная колонка названа выше.
        foreach (var view in Views ?? [])
        {
            if (view is null) { problems.Add("в списке представлений пустое место"); continue; }
            problems.AddRange(view.Problems(Columns));
        }
        foreach (var twice in (Views ?? []).Where(v => v is not null)
                     .GroupBy(v => v.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"представление «{twice.Key}» объявлено дважды");

        return problems;
    }
}
