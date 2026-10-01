using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Состав колонок предпросмотра привязки (задача G1a, issue #1088): из схемы типа строки, а не из
/// первой строки данных.
/// </summary>
public class BindingPreviewColumnsTests
{
    private static SchemaFieldInfo F(string key, string? title = null) => new(key, "string", null, title);

    [Fact]
    public void Колонки_идут_в_порядке_схемы_и_с_её_заголовками()
    {
        var columns = BindingPreviewColumns.Build(
            ["Б", "А"], [F("А", "Первое"), F("Лишнее"), F("Б")]);

        Assert.Equal(["А", "Б"], columns.Select(c => c.Key));
        Assert.Equal(["Первое", "Б"], columns.Select(c => c.Label));
        Assert.All(columns, c => Assert.Null(c.Unavailable));
    }

    /// <summary>Разметка на удалённое поле — колонка остаётся и называет причину, а не пропадает.</summary>
    [Fact]
    public void Поле_которого_нет_в_типе_остаётся_колонкой_с_причиной()
    {
        var columns = BindingPreviewColumns.Build(["А", "Удалённое"], [F("А")]);

        var removed = Assert.Single(columns, c => c.Key == "Удалённое");
        Assert.Equal(BindingPreviewColumns.Removed, removed.Unavailable);
    }

    /// <summary>Тип строки неизвестен — «не знаем» не выдаётся за «поля нет».</summary>
    [Fact]
    public void Без_типа_строки_колонки_без_пометок()
    {
        var columns = BindingPreviewColumns.Build(["А", "Б"], fields: null);

        Assert.Equal(["А", "Б"], columns.Select(c => c.Key));
        Assert.All(columns, c => Assert.Null(c.Unavailable));
    }
}
