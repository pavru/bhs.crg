using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Четыре узла дерева условий и сравнение по виду колонки (задача G1c, issue #1090, ТЗ CORE-33).
/// Согласие исполнителя в памяти с запросом к базе сторожит <c>ModuleTableFilterPairTests</c>; здесь —
/// правила сами по себе, без базы.
/// </summary>
public class TableConditionsTests
{
    private static List<IReadOnlyDictionary<string, string?>> Rows(string column, params string?[] cells) =>
        [.. cells.Select(c => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { [column] = c })];

    private static string One(string column, string op, string value) =>
        $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}""";

    private static string Many(string column, string op, params string[] values) =>
        $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","values":[{{string.Join(",", values.Select(v => $"\"{v}\""))}}]}""";

    private static DataSetColumnTypes Typed(string column, string kind) =>
        new(new Dictionary<string, string> { [column] = kind }, new Dictionary<string, string>());

    private static DataSetColumnTypes Choice(string column, params string[] options) =>
        new(new Dictionary<string, string> { [column] = TableOperators.Choice }, new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<string>> { [column] = options });

    // ── Перечень: условие по дочернему зерну ───────────────────────────────────

    /// <summary>
    /// «Есть в перечне такой» — у положительных операторов; отрицание — «нет НИ ОДНОГО такого», а не
    /// «есть хоть один другой»: счёт, разнесённый на А и на Б, под «объект не А» не попадает.
    /// </summary>
    [Fact]
    public void Условие_по_перечню_спрашивает_есть_ли_такой_элемент()
    {
        var types = Typed("О", TableOperators.List);
        var rows = Rows("О", "Комарова 36, 4 эт.\nСклад", "Склад", "Ливнёвка", null);

        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "набор", types).Count;

        Assert.Equal(2, Count(One("О", "eq", "склад")));
        Assert.Equal(2, Count(One("О", "neq", "склад")));
        Assert.Equal(1, Count(One("О", "contains", "КОМАР")));
        Assert.Equal(3, Count(One("О", "not_contains", "комар")));
        Assert.Equal(1, Count(One("О", "starts_with", "лив")));
        Assert.Equal(3, Count(Many("О", "in", "Ливнёвка", "Склад")));
        Assert.Equal(1, Count(Many("О", "not_in", "Ливнёвка", "Склад")));
        Assert.Equal(1, Count("""{"type":"condition","column":"О","op":"is_empty"}"""));
        Assert.Equal(3, Count("""{"type":"condition","column":"О","op":"is_not_empty"}"""));

        // Запятая — часть названия, а не разделитель: перечень делит перевод строки.
        Assert.Equal(1, Count(One("О", "eq", "Комарова 36, 4 эт.")));
        Assert.Equal(0, Count(One("О", "eq", "Комарова 36")));
    }

    [Fact]
    public void Клетка_перечня_собирается_и_разбирается_одним_разделителем()
    {
        Assert.Equal(["Комарова 36, 4 эт.", "Склад"], TableConditions.Items(TableConditions.Join(["Комарова 36, 4 эт.", "Склад"])));
        Assert.Null(TableConditions.Join([]));
        Assert.Empty(TableConditions.Items(null));
    }

    // ── Какие объекты отбор НАЗЫВАЕТ ───────────────────────────────────────────

    private static TableFilterCondition On(string column, string op, params string[] values) =>
        new(column, ModuleTableColumnKind.List, op, values, TableConditions.Compile(TableOperators.List, op, values));

    private static TableFilterGroup All(params TableFilter[] children) => new(false, children);

    private static TableFilterGroup Any(params TableFilter[] children) => new(true, children);

    /// <summary>
    /// Долю счёта считают на объекты, которые отбор назвал, — и названо только то, без чего строка в
    /// отбор не попала бы. Иначе «доля на А» появилась бы у счёта, на А не разнесённого.
    /// </summary>
    [Fact]
    public void Отбор_называет_объект_только_когда_без_него_строка_не_попала_бы_в_отбор()
    {
        var a = On("О", "eq", "А");
        var b = On("О", "in", "Б", "В");
        var other = On("Номер", "contains", "1");

        Assert.Equal([a], TableFilters.Naming(a, "О"));
        Assert.Equal([a], TableFilters.Naming(All(other, a), "О"));
        Assert.Equal([a, b], TableFilters.Naming(All(a, All(other, b)), "О"));
        Assert.Equal([a, b], TableFilters.Naming(All(other, Any(a, b)), "О"));

        // «А или что-то ещё» объекта не называет: в отборе есть строки без А.
        Assert.Empty(TableFilters.Naming(Any(a, other), "О"));
        Assert.Empty(TableFilters.Naming(All(other, Any(a, other)), "О"));
        // Отрицание и «пусто» не называют ничего.
        Assert.Empty(TableFilters.Naming(On("О", "neq", "А"), "О"));
        Assert.Empty(TableFilters.Naming(On("О", "not_in", "А", "Б"), "О"));
        Assert.Empty(TableFilters.Naming(On("О", "is_not_empty"), "О"));
        // Нет отбора или условие по другой колонке.
        Assert.Empty(TableFilters.Naming(null, "О"));
        Assert.Empty(TableFilters.Naming(other, "О"));
        Assert.Empty(TableFilters.Naming(Any(), "О"));
    }

    /// <summary>
    /// Часть строки под отбором: спрашивают пару значений, а не два списка. Чужие колонки, отрицания и
    /// «пусто» части не отсеивают — строка под отбор уже попала.
    /// </summary>
    [Fact]
    public void Отбор_допускает_часть_строки_по_паре_значений_а_не_по_двум_спискам()
    {
        static Dictionary<string, string?> Part(string? o, string? m) => new() { ["О"] = o, ["М"] = m };
        var pairs = Any(All(On("О", "eq", "А"), On("М", "eq", "09")), All(On("О", "eq", "Б"), On("М", "eq", "10")));

        Assert.True(TableFilters.Admits(pairs, Part("А", "09")));
        Assert.True(TableFilters.Admits(pairs, Part("Б", "10")));
        Assert.False(TableFilters.Admits(pairs, Part("А", "10")));
        // Значения у части нет (остаток счёта не лежит ни на одном объекте) — условие по нему не выполнено.
        Assert.False(TableFilters.Admits(On("О", "eq", "А"), Part(null, "09")));
        Assert.True(TableFilters.Admits(On("М", "eq", "09"), Part(null, "09")));

        // Два условия по одной колонке «все разом» — альтернативы: их исполняют две разные части.
        var both = All(On("М", "eq", "09"), All(On("М", "eq", "10"), On("О", "eq", "А")));
        Assert.True(TableFilters.Admits(both, Part("А", "09")));
        Assert.True(TableFilters.Admits(both, Part("А", "10")));
        Assert.False(TableFilters.Admits(both, Part("А", "11")));
        Assert.False(TableFilters.Admits(both, Part("Б", "10")));

        // По одной колонке допуск и «названо отбором» — один ответ.
        var named = All(On("О", "eq", "А"), On("О", "in", "Б", "В"), On("Номер", "contains", "1"));
        foreach (var value in new[] { "А", "Б", "В", "Г" })
            Assert.Equal(
                TableFilters.Naming(named, "О").Any(c => c.Matches(value)),
                TableFilters.Admits(named, new Dictionary<string, string?> { ["О"] = value }));

        Assert.True(TableFilters.Admits(null, Part("А", "09")));
        Assert.True(TableFilters.Admits(On("Номер", "contains", "1"), Part("А", "09")));
        Assert.True(TableFilters.Admits(On("О", "neq", "А"), Part("А", "09")));
        Assert.True(TableFilters.Admits(On("О", "is_empty"), Part("А", "09")));
        Assert.True(TableFilters.Admits(Any(On("О", "eq", "Б"), On("Номер", "contains", "1")), Part("А", "09")));
    }

    // ── Узлы у файловых наборов: та же догадка, что у остальных сравнений ──────

    [Fact]
    public void Перечень_период_и_неопределённость_работают_у_файлового_набора()
    {
        var rows = Rows("К", "10", "5", "кабель", "", null);

        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Many("К", "in", "10", "КАБЕЛЬ"), rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(Many("К", "not_in", "10", "КАБЕЛЬ"), rows).Count);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Many("К", "between", "5", "10"), rows).Count);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply("""{"type":"condition","column":"К","op":"is_null"}""", rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("""{"type":"condition","column":"К","op":"is_not_null"}""", rows).Count);
    }

    [Theory]
    [InlineData("""{"type":"condition","column":"К","op":"between","values":["1"]}""", "две границы")]
    [InlineData("""{"type":"condition","column":"К","op":"between","value":"1"}""", "две границы")]
    [InlineData("""{"type":"condition","column":"К","op":"in"}""", "список значений")]
    [InlineData("""{"type":"condition","column":"К","op":"in","values":[]}""", "список значений")]
    public void Узел_без_нужных_значений_отказывает(string filter, string reason)
    {
        var refusal = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(filter, Rows("К", "1")));
        Assert.Contains(reason, refusal.Message);
    }

    /// <summary>У файлового набора поведение прежнее: на догадке стоят сохранённые отборы.</summary>
    [Fact]
    public void Без_видов_колонок_сравнение_идёт_по_догадке_как_прежде()
    {
        var rows = Rows("Сумма", "110.00", "", "12 шт");

        // Строки разные — «равно» не находит; пустая строка и «12 шт» «меньше» 500 как строки.
        Assert.Empty(DataSetRowFilterExecutor.Apply(One("Сумма", "eq", "110"), rows));
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(One("Сумма", "lt", "500"), rows).Count);
    }

    // ── Сравнение по виду ─────────────────────────────────────────────────────

    [Fact]
    public void С_видом_число_сравнивается_числом_а_клетка_с_текстом_под_сравнение_не_попадает()
    {
        var rows = Rows("Сумма", "110.00", "", "12 шт", null, "99.5");
        var types = Typed("Сумма", TableOperators.Number);
        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "т", types).Count;

        Assert.Equal(1, Count(One("Сумма", "eq", "110")));
        Assert.Equal(1, Count(One("Сумма", "lt", "100")));          // 99.5; пустые и «12 шт» — нет
        Assert.Equal(4, Count(One("Сумма", "neq", "110")));         // всё, что не 110, включая пустые и текст
        Assert.Equal(2, Count(Many("Сумма", "between", "99.5", "110")));
        Assert.Equal(2, Count("""{"type":"condition","column":"Сумма","op":"is_null"}"""));
        // «is_empty» у числа — прежнее имя того же вопроса: сохранённые отборы обязаны работать.
        Assert.Equal(2, Count("""{"type":"condition","column":"Сумма","op":"is_empty"}"""));
    }

    [Fact]
    public void С_видом_дата_сравниваются_первые_десять_знаков_а_не_дата_под_сравнение_не_попадает()
    {
        var rows = Rows("Срок", "2026-05-01", "2026-05-01T12:00:00", "скоро", null, "2026-06-01");
        var types = Typed("Срок", TableOperators.Date);
        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "т", types).Count;

        Assert.Equal(2, Count(One("Срок", "eq", "2026-05-01")));
        Assert.Equal(2, Count(One("Срок", "lt", "2026-06-01")));
        Assert.Equal(1, Count(One("Срок", "gt", "2026-05-01")));
    }

    [Theory]
    [InlineData("number", "contains", "1", "не применяется")]
    [InlineData("number", "gt", "много", "не число")]
    [InlineData("number", "gt", "1,5", "не число")]
    [InlineData("date", "lt", "01.05.2026", "не дата")]
    [InlineData("date", "lt", "2026-13-45", "не дата")]
    [InlineData("boolean", "eq", "да", "не «true»")]
    [InlineData("text", "between", "а", "не применяется")]
    public void Условие_негодное_для_вида_колонки_отказывает(string kind, string op, string value, string reason)
    {
        var filter = op == "between" ? Many("К", op, value, value) : One("К", op, value);

        var refusal = Assert.Throws<ConflictException>(() =>
            DataSetRowFilterExecutor.Apply(filter, Rows("К", "1"), "т", Typed("К", kind)));
        Assert.Contains(reason, refusal.Message);
    }

    /// <summary>
    /// Колонка-выбор (G1d, issue #1091): значение вне её перечня — ОТКАЗ с названным перечнем, а не
    /// «ничего не нашлось». У текста опечатка в состоянии выглядела бы как «таких счетов нет».
    /// </summary>
    [Fact]
    public void Значение_вне_перечня_колонки_выбора_отказывает_и_называет_перечень()
    {
        var rows = Rows("Оплата", "Не оплачен", "Оплачен", "Оплачен", null);
        var types = Choice("Оплата", "Не оплачен", "Оплачен");
        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "т", types).Count;
        string Refusal(string filter) => Assert.Throws<ConflictException>(() => Count(filter)).Message;

        Assert.Equal(2, Count(One("Оплата", "eq", "Оплачен")));
        Assert.Equal(2, Count(One("Оплата", "neq", "Оплачен")));
        Assert.Equal(3, Count(Many("Оплата", "in", "Оплачен", "Не оплачен")));

        var typo = Refusal(One("Оплата", "eq", "Оплочен"));
        Assert.Contains("значения «Оплочен» в перечне колонки нет", typo);
        Assert.Contains("«Не оплачен», «Оплачен»", typo);

        // Перечень сверяется буква в букву: значение выбирают из списка, а не набирают.
        Assert.Contains("в перечне колонки нет", Refusal(One("Оплата", "eq", "оплачен")));
        // Одно негодное значение в списке — негоден весь список.
        Assert.Contains("«Чепуха»", Refusal(Many("Оплата", "in", "Оплачен", "Чепуха")));
        // Операторов текста у выбора нет: «содержит опл» — это уже набранная строка.
        Assert.Contains("не применяется", Refusal(One("Оплата", "contains", "Опл")));
    }

    /// <summary>
    /// Выбор, которому перечня не дали, отвергает любое значение — а не принимает любое. Иначе забытый
    /// перечень превращал бы колонку в текст, и опечатка снова молча ничего не находила бы.
    /// </summary>
    [Fact]
    public void Выбор_без_перечня_отвергает_любое_значение()
    {
        Assert.NotNull(TableConditions.Problem(TableOperators.Choice, "eq", ["Оплачен"]));
        Assert.NotNull(TableConditions.Problem(TableOperators.Choice, "eq", ["Оплачен"], []));
        Assert.Null(TableConditions.Problem(TableOperators.Choice, "eq", ["Оплачен"], ["Оплачен"]));
    }

    /// <summary>Колонка без объявленного вида (вычисляемая) — по догадке и в типизированном наборе.</summary>
    [Fact]
    public void Колонка_без_вида_в_типизированном_наборе_сравнивается_по_догадке()
    {
        var rows = Rows("Расчёт", "110.00", "");
        var types = Typed("Другая", TableOperators.Number);

        Assert.Empty(DataSetRowFilterExecutor.Apply(One("Расчёт", "eq", "110"), rows, "т", types));
    }

    // ── Договор двух исполнителей ─────────────────────────────────────────────

    /// <summary>
    /// Запись числа и даты — одна у обоих исполнителей. Проект контрактов наших проектов не видит,
    /// поэтому у запроса к базе своя копия; разойдись они — клетку «1e3» один исполнитель счёл бы
    /// числом, другой текстом.
    /// </summary>
    [Fact]
    public void Запись_числа_и_даты_у_исполнителей_одна()
    {
        Assert.Equal(TableConditions.NumberPattern, TableSqlRules.NumberPattern);
        Assert.Equal(TableConditions.DatePattern, TableSqlRules.DatePattern);
    }

    /// <summary>
    /// Запросу описаны ВСЕ объявленные колонки — и проверяется это при СБОРКЕ описания, то есть на любом
    /// чтении таблицы. Забытая ушла бы в поля схемы, отбор по ней искал бы ключ в данных записи и
    /// отвечал «ничего не найдено»; а проверка «при первом отборе» молчала бы, пока таблицу только читают.
    /// </summary>
    [Fact]
    public void Описание_без_объявленной_колонки_отказывает_при_сборке_и_называет_её()
    {
        var table = new ModuleTable(
            "probe", "Проба", "запись", "probe", ModuleTableIsolation.None, "Отдаёт всё",
            [
                new("Номер", "Номер", ModuleTableColumnKind.Text),
                new("Сумма", "Сумма", ModuleTableColumnKind.Number),
                new("Срочно", "Срочно", ModuleTableColumnKind.Boolean),
            ],
            typeof(object));

        var missing = Assert.Throws<InvalidOperationException>(() => TableSql<Probe>.Describe(table, sql => sql
            .Text("Номер", p => p.Number).Flag("Срочно", p => p.Urgent).Fields(_ => p => p.Number)));
        Assert.Contains("«Сумма»", missing.Message);
        Assert.DoesNotContain("«Номер»", missing.Message);

        // Колонка, описанная не тем видом, — тоже: число, описанное текстом, сравнивалось бы строками.
        var wrong = Assert.Throws<InvalidOperationException>(() => TableSql<Probe>.Describe(table, sql => sql
            .Text("Номер", p => p.Number).Text("Сумма", p => p.Number).Flag("Срочно", p => p.Urgent)));
        Assert.Contains("«Сумма»", wrong.Message);

        // Полное описание собирается — в том числе колонка-флаг: у каждого объявляемого вида есть чем её описать.
        TableSql<Probe>.Describe(table, sql => sql
            .Text("Номер", p => p.Number).Number("Сумма", p => p.Total).Flag("Срочно", p => p.Urgent));
    }

    /// <summary>
    /// У колонки-выбора перечень объявления и слова, которые знает запрос, — одно множество (G1d, issue
    /// #1091). Экран предлагает первое, запрос находит по второму: разойдись они, выбранное из списка
    /// значение молча не находило бы ничего.
    /// </summary>
    [Fact]
    public void Перечень_колонки_выбора_обязан_совпасть_со_словами_запроса()
    {
        var table = new ModuleTable(
            "probe", "Проба", "запись", "probe", ModuleTableIsolation.None, "Отдаёт всё",
            [new("Вид", "Вид", ModuleTableColumnKind.Choice, Options: ["Первый", "Второй"])],
            typeof(object));
        TableSql<Probe> With(Dictionary<int, string> words) =>
            TableSql<Probe>.Describe(table, sql => sql.Choice("Вид", p => p.Kind, words));

        With(new() { [1] = "Второй", [2] = "Первый" });

        var missing = Assert.Throws<InvalidOperationException>(() => With(new() { [1] = "Первый" }));
        Assert.Contains("«Вид»", missing.Message);
        Assert.Throws<InvalidOperationException>(() => With(new() { [1] = "Первый", [2] = "Второй", [3] = "Третий" }));
        // Справочником выбор не описать: вид другой, и перечень некому было бы сверить.
        Assert.Throws<InvalidOperationException>(() =>
            TableSql<Probe>.Describe(table, sql => sql.Lookup("Вид", p => p.Kind, new Dictionary<int, string>())));
    }

    private sealed class Probe
    {
        public string? Number { get; set; }
        public decimal? Total { get; set; }
        public bool? Urgent { get; set; }
        public int? Kind { get; set; }
    }

    // ── Число значений — одна проверка на оба режима ──────────────────────────

    /// <summary>
    /// «Равно» с двумя значениями (перепутали с перечнем) отказывает и у файлового набора: иначе
    /// сравнение шло бы с первым значением, строки второго пропадали бы, а выдача выглядела бы правильной.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"condition","column":"К","op":"eq","values":["а","б"]}""", "одно значение")]
    [InlineData("""{"type":"condition","column":"К","op":"is_empty","values":["а"]}""", "значения не принимает")]
    [InlineData("""{"type":"condition","column":"К","op":"in","values":[null]}""", "пустое место")]
    [InlineData("""{"type":"condition","column":"К","op":"contains","values":[null]}""", "пустое место")]
    [InlineData("""{"type":"condition","column":"К","op":"eq","value":"а","values":["б"]}""", "дважды")]
    public void Негодное_число_значений_отказывает_в_обоих_режимах(string filter, string reason)
    {
        var byGuess = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(filter, Rows("К", "а")));
        Assert.Contains(reason, byGuess.Message);

        var byKind = Assert.Throws<ConflictException>(() =>
            DataSetRowFilterExecutor.Apply(filter, Rows("К", "а"), "т", Typed("К", TableOperators.Text)));
        Assert.Contains(reason, byKind.Message);
    }

    /// <summary>Отборы, сохранённые до G1c, несут пустое «value» у «пусто» — и обязаны работать.</summary>
    [Fact]
    public void Прежняя_запись_условия_без_значения_работает()
    {
        var rows = Rows("К", "а", "", null);

        Assert.Equal(2, DataSetRowFilterExecutor.Apply(One("К", "is_empty", ""), rows).Count);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply("""{"type":"condition","column":"К","op":"eq"}""", rows).Count);
    }
}
