using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.Tables;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Tests.Configuration;

/// <summary>Форма сохранённого представления таблицы (задача G3a, issue #1095).</summary>
public class SavedTableViewRulesTests
{
    /// <summary>
    /// Слова итогов записаны дважды: у готовых представлений модуля и у сохранённых — проекты друг на
    /// друга не ссылаются. Разойдись они, итог, который модуль объявляет в своём представлении, не
    /// сохранялся бы копией — отказом «не считается» на исправном представлении.
    /// </summary>
    [Fact]
    public void Слова_итогов_те_же_что_у_представлений_модуля() =>
        Assert.Equal(ModuleTableView.Aggregates, SavedTableViewRules.Aggregates);

    [Fact]
    public void Состояние_приводится_к_одному_виду_и_переживает_хранение()
    {
        var (state, problem) = SavedTableViewRules.Normalize(new TableViewState(
            [" Номер ", "Дата"], "  ", [new(" Дата ", true)], [new("Номер", "count")], 1));

        Assert.Null(problem);
        Assert.Equal(["Номер", "Дата"], state.Columns);
        Assert.Null(state.Filter);
        Assert.Equal(new TableSortRequest("Дата", true), Assert.Single(state.Sort!));

        var read = SavedTableViewRules.Read(SavedTableViewRules.Store(state));
        Assert.Equal(state.Columns, read.Columns);
        Assert.Equal(state.Sort, read.Sort);
        Assert.Equal(state.Totals, read.Totals);
        Assert.Equal(1, read.Pinned);
    }

    /// <summary>«Все колонки таблицы» — null, а не пустой список: пустой означал бы таблицу без колонок.</summary>
    [Fact]
    public void Пустое_состояние_годно_и_значит_все_колонки()
    {
        var (state, problem) = SavedTableViewRules.Normalize(null);

        Assert.Null(problem);
        Assert.Null(state.Columns);
        Assert.Empty(state.Sort!);
        Assert.Empty(state.Totals!);
    }

    [Fact]
    public void Слишком_большое_состояние_отвергается()
    {
        var filter = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "condition", column = "Поставщик", op = "in",
            values = Enumerable.Range(0, 4000).Select(i => $"Поставщик {i}").ToArray(),
        });

        var (_, problem) = SavedTableViewRules.Normalize(new TableViewState(Filter: filter));

        Assert.Contains("КБ", problem);
    }

    [Theory]
    [InlineData("Номер,Номер", "названа дважды")]
    [InlineData("Номер,", "пустое место")]
    public void Негодный_список_колонок_назван(string columns, string expected)
    {
        var (_, problem) = SavedTableViewRules.Normalize(new TableViewState(columns.Split(',')));

        Assert.Contains(expected, problem);
    }
}
