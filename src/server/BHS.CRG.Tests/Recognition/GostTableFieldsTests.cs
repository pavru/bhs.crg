using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostTableFieldsTests
{
    [Fact]
    public void ColumnsForTag_MapsKnownTags()
    {
        Assert.Same(GostTableFields.SpecificationColumns, GostTableFields.ColumnsForTag(FunctionalTag.GostDocSpecification));
        Assert.Same(GostTableFields.CableJournalColumns, GostTableFields.ColumnsForTag(FunctionalTag.GostDocCableJournal));
        Assert.Null(GostTableFields.ColumnsForTag("что-то другое"));
    }

    [Fact]
    public void RecognitionFieldsFor_AppendsRowsArrayField()
    {
        var fields = GostTableFields.RecognitionFieldsFor(GostTableFields.CableJournalColumns);
        Assert.Equal(GostTableFields.CableJournalColumns.Count + 1, fields.Count);
        Assert.Contains(fields, f => f.Path == GostTableFields.RowsPath);
    }

    [Fact]
    public void SplitRows_NormalizesToColumns_DropsExtraAndEmpty()
    {
        var cols = GostTableFields.CableJournalColumns;
        var json = """
        [
          {"НомерКабеля":"1","Начало":"ЩВ","Конец":"Розетки","МаркаПроект":"ВВГнг","лишнее":"игнор"},
          {"НомерКабеля":"","Начало":"","Конец":""},
          {"НомерКабеля":"2","Конец":"Свет"}
        ]
        """;
        var values = new Dictionary<string, string?> { [GostTableFields.RowsPath] = json };

        var rows = GostTableFields.SplitRows(values, cols);

        Assert.Equal(2, rows.Count); // полностью пустая строка отброшена
        Assert.Equal("1", rows[0]["НомерКабеля"]);
        Assert.Equal("ВВГнг", rows[0]["МаркаПроект"]);
        Assert.False(rows[0].ContainsKey("лишнее")); // лишний ключ отброшен
        Assert.Null(rows[1]["Начало"]); // недостающая ячейка → null
        Assert.Equal("Свет", rows[1]["Конец"]);
    }

    // ── Суперсет кабельного журнала (issue #389) ──────────────────────────────

    [Fact]
    public void CableJournalColumns_SupersetHasProjectAndFactPairs()
    {
        var keys = GostTableFields.CableJournalColumns.Select(c => c.Path).ToList();
        string[] expected =
        [
            "НомерКабеля", "Начало", "Конец", "Участок",
            "МаркаПроект", "СечениеПроект", "ДлинаПроект",
            "МаркаФакт", "СечениеФакт", "ДлинаФакт",
            "СпособПрокладки", "Примечание",
        ];
        Assert.Equal(expected, keys); // порядок и состав суперсета
    }

    [Fact]
    public void BuildCableJournalPrompt_DescribesProjectFactMapping()
    {
        var p = RecognitionShared.BuildCableJournalPrompt(
            GostTableFields.RecognitionFieldsFor(GostTableFields.CableJournalColumns));
        Assert.Contains("Проложен", p);   // фактическая секция описана (её терял общий промпт)
        Assert.Contains("*Проект", p);
        Assert.Contains("*Факт", p);
        Assert.Contains(GostTableFields.RowsPath, p);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("")]
    [InlineData(null)]
    public void SplitRows_BrokenOrMissing_ReturnsEmpty(string? json)
    {
        var values = new Dictionary<string, string?> { [GostTableFields.RowsPath] = json };
        Assert.Empty(GostTableFields.SplitRows(values, GostTableFields.SpecificationColumns));
    }
}
