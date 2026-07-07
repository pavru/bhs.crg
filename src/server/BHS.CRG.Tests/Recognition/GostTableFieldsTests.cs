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
          {"Номер":"1","Откуда":"ЩВ","Куда":"Розетки","МаркаКабеля":"ВВГнг","лишнее":"игнор"},
          {"Номер":"","Откуда":"","Куда":""},
          {"Номер":"2","Куда":"Свет"}
        ]
        """;
        var values = new Dictionary<string, string?> { [GostTableFields.RowsPath] = json };

        var rows = GostTableFields.SplitRows(values, cols);

        Assert.Equal(2, rows.Count); // полностью пустая строка отброшена
        Assert.Equal("1", rows[0]["Номер"]);
        Assert.Equal("ВВГнг", rows[0]["МаркаКабеля"]);
        Assert.False(rows[0].ContainsKey("лишнее")); // лишний ключ отброшен
        Assert.Null(rows[1]["Откуда"]); // недостающая ячейка → null
        Assert.Equal("Свет", rows[1]["Куда"]);
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
