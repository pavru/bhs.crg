using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Configuration;

/// <summary>Отбор таблицы словами — шапка выгруженного файла (задача G3b, issue #1096).</summary>
public class TableFilterWordsTests
{
    private static readonly TableColumnDto[] Columns =
    [
        new("Поставщик", "Поставщик", TableOperators.Text, [], true),
        new("Итого", "Итого", TableOperators.Number, [], true),
        new("Срок", "Оплатить до", TableOperators.Date, [], true),
        new("Оплачен", "Оплачен", TableOperators.Boolean, [], true),
    ];

    private static FilterNode Condition(string column, string op, params string[] values) => values.Length == 1
        ? new() { Type = "condition", Column = column, Op = op, Value = values[0] }
        : new() { Type = "condition", Column = column, Op = op, Values = values };

    private static FilterNode Group(string logic, params FilterNode[] children) =>
        new() { Type = "group", Logic = logic, Children = children };

    [Fact]
    public void Nothing_to_say_without_filter() =>
        Assert.Null(TableFilterWords.Describe(null, Columns));

    [Fact]
    public void Empty_group_restricts_nothing() =>
        Assert.Null(TableFilterWords.Describe(Group("and"), Columns));

    [Fact]
    public void Nested_group_is_bracketed_so_the_logic_reads_as_executed()
    {
        var words = TableFilterWords.Describe(Group("and",
            Condition("Поставщик", "eq", "Электросила"),
            Group("or", Condition("Итого", "gt", "100000"), Condition("Срок", "is_null"))), Columns);

        Assert.Equal("Поставщик: «Электросила» и (Итого > 100000 или Оплатить до: не определено)", words);
    }

    [Fact]
    public void Values_read_as_on_screen()
    {
        Assert.Equal("Оплатить до: от 01.10.2026 до 31.10.2026",
            TableFilterWords.Describe(Condition("Срок", "between", "2026-10-01", "2026-10-31"), Columns));
        Assert.Equal("Оплачен: нет", TableFilterWords.Describe(Condition("Оплачен", "eq", "false"), Columns));
        Assert.Equal("Поставщик — кроме: «А», «Б»",
            TableFilterWords.Describe(Condition("Поставщик", "not_in", "А", "Б"), Columns));
    }

    [Fact]
    public void Column_and_operator_unknown_here_are_named_not_dropped()
    {
        // Пропуск сделал бы отбор в шапке шире, чем он есть.
        Assert.Equal("НетТакой новое «1»", TableFilterWords.Describe(Condition("НетТакой", "новое", "1"), Columns));
    }
}
