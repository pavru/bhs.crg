using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetRowFilterExecutorTests
{
    private static List<IReadOnlyDictionary<string, string?>> Rows(params (string col, string? val)[][] rows) =>
        rows.Select(r => (IReadOnlyDictionary<string, string?>)r.ToDictionary(c => c.col, c => c.val)).ToList();

    private static List<IReadOnlyDictionary<string, string?>> Sample() => Rows(
        [("Тип", "Кабель"), ("Кол", "10")],
        [("Тип", "Лоток"), ("Кол", "5")],
        [("Тип", "Кабель"), ("Кол", "0")]);

    private static string Condition(string column, string op, string? value = null) =>
        value is null
            ? $$"""{"type":"condition","column":"{{column}}","op":"{{op}}"}"""
            : $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}""";

    private static string Group(string logic, params string[] children) =>
        $$"""{"type":"group","logic":"{{logic}}","children":[{{string.Join(",", children)}}]}""";

    [Fact]
    public void NullOrEmptyFilter_ReturnsAllRows()
    {
        var rows = Sample();
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(null, rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("", rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("   ", rows).Count);
    }

    [Fact]
    public void EmptyGroup_ReturnsAllRows()
    {
        var result = DataSetRowFilterExecutor.Apply(Group("and"), Sample());
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SingleEqCondition_Filters()
    {
        var json = Group("and", Condition("Тип", "eq", "Кабель"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Кабель", r["Тип"]));
    }

    [Fact]
    public void AndLogic_RequiresAllConditions()
    {
        var json = Group("and", Condition("Тип", "eq", "Кабель"), Condition("Кол", "gt", "5"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Single(result);
        Assert.Equal("10", result[0]["Кол"]);
    }

    [Fact]
    public void OrLogic_RequiresAnyCondition()
    {
        var json = Group("or", Condition("Тип", "eq", "Лоток"), Condition("Кол", "eq", "0"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NestedGroups_CombineLogic()
    {
        // Тип == Кабель AND (Кол == 10 OR Кол == 0)
        var inner = Group("or", Condition("Кол", "eq", "10"), Condition("Кол", "eq", "0"));
        var json = Group("and", Condition("Тип", "eq", "Кабель"), inner);
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Кабель", r["Тип"]));
    }

    [Theory]
    [InlineData("eq", "Кабель", 2)]
    [InlineData("neq", "Кабель", 1)]
    [InlineData("contains", "абель", 2)]
    [InlineData("not_contains", "абель", 1)]
    [InlineData("starts_with", "Ка", 2)]
    [InlineData("ends_with", "ток", 1)]
    public void StringOperators(string op, string value, int expected)
    {
        var json = Group("and", Condition("Тип", op, value));
        Assert.Equal(expected, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Theory]
    [InlineData("gt", "5", 1)]   // 10
    [InlineData("gte", "5", 2)]  // 10, 5
    [InlineData("lt", "5", 1)]   // 0
    [InlineData("lte", "5", 2)]  // 5, 0
    public void NumericComparison_UsesNumbers(string op, string value, int expected)
    {
        var json = Group("and", Condition("Кол", op, value));
        Assert.Equal(expected, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Fact]
    public void EqIsCaseInsensitive()
    {
        var json = Group("and", Condition("Тип", "eq", "кабель"));
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Fact]
    public void IsEmptyAndIsNotEmpty()
    {
        var rows = Rows([("A", "x")], [("A", "")], [("A", null)]);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Group("and", Condition("A", "is_empty")), rows).Count);
        Assert.Single(DataSetRowFilterExecutor.Apply(Group("and", Condition("A", "is_not_empty")), rows));
    }

    [Fact]
    public void MissingColumn_TreatedAsEmptyString()
    {
        var rows = Rows([("A", "x")]);
        // column "B" not present → empty → is_empty matches, eq "x" does not
        Assert.Single(DataSetRowFilterExecutor.Apply(Group("and", Condition("B", "is_empty")), rows));
        Assert.Empty(DataSetRowFilterExecutor.Apply(Group("and", Condition("B", "eq", "x")), rows));
    }

    // ── Отбор, который нельзя выполнить, отказывает (issue #966) ──────────────────
    //
    // До 0.199.1 оба случая «проходили успешно»: испорченное описание возвращало ВСЕ строки, как
    // будто отбора нет вовсе, а неизвестный оператор считался истиной — то есть условие молча
    // пропускало строки насквозь. Выдача при этом выглядела обычной, и расхождение с экраном
    // обнаружилось бы после подписи.

    [Theory]
    // Ломаный синтаксис. В базе такого не бывает вовсе — колонка jsonb, и негодный текст в неё не
    // ложится; проверяется потому, что исполнителю всё равно, откуда пришла строка.
    [InlineData("{ not valid json")]
    // А ВОТ ЭТО в базе лежать может: JSON годный, а форма чужая — отбор, сохранённый как строка или
    // как массив условий вместо корневой группы. Именно так «испорченное описание» и выглядит на
    // живых данных, и ровно этот случай прежде возвращал все строки.
    [InlineData("\"Тип = Кабель\"")]
    [InlineData("""[{"type":"condition","column":"Тип","op":"eq","value":"Кабель"}]""")]
    public void Испорченное_описание_отказ_а_не_все_строки(string json)
    {
        var refusal = Assert.Throws<ConflictException>(
            () => DataSetRowFilterExecutor.Apply(json, Sample(), "Материалы"));
        // Источник назван: у документа привязок бывает пять, и «отбор не разбирается» без имени
        // не говорит, какой из них править.
        Assert.Contains("Материалы", refusal.Message);
    }

    [Fact]
    public void Неизвестный_оператор_отказ_с_указанием_условия()
    {
        var json = Group("and", Condition("Кол", "betwen", "5"));
        var refusal = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(json, Sample()));
        Assert.Contains("betwen", refusal.Message);
        Assert.Contains("Кол", refusal.Message);
    }

    [Fact]
    public void Место_условия_в_отказе_названо_путём()
    {
        // Вложенное условие 2.1: колонки в дереве повторяются, и одной колонки для «какое именно
        // условие» не хватает.
        var inner = Group("or", Condition("Кол", "betwen", "5"));
        var json = Group("and", Condition("Тип", "eq", "Кабель"), inner);
        var refusal = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(json, Sample()));
        Assert.Contains("2.1", refusal.Message);
    }

    [Fact]
    public void Отказ_приходит_и_на_пустом_наборе_строк()
    {
        // Дерево проверяется ДО первой строки. Иначе у источника, который сегодня вернул ноль строк,
        // битый оператор молчал бы — и сторож оказался бы недостижим ровно в том состоянии, в
        // котором его труднее всего заметить глазами.
        var json = Group("and", Condition("Кол", "betwen", "5"));
        Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(json, []));
    }

    [Theory]
    // Опечатка в виде узла: «conditon» прежде считался группой без условий, то есть истиной.
    [InlineData("""{"type":"conditon","column":"Тип","op":"eq","value":"Кабель"}""", "conditon")]
    // Логика, которой нет: «xor» прежде молча становился «and».
    [InlineData("""{"type":"group","logic":"xor","children":[]}""", "xor")]
    // Условие без колонки: сравнивать не с чем, а прежде сравнивалось с пустой строкой.
    [InlineData("""{"type":"condition","op":"eq","value":"Кабель"}""", "колонк")]
    // «null» — описание есть, условий в нём нет: отсутствием отбора это не считается.
    [InlineData("null", "null")]
    public void Негодное_описание_отказывает(string json, string expectedInMessage)
    {
        var refusal = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(json, Sample()));
        Assert.Contains(expectedInMessage, refusal.Message);
    }
}
