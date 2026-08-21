using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Происхождение значений источника — свойство самого источника, а не чья-то частная классификация.
/// До этого правило жило приватным методом в сервисе снимков плюс вторым вызовом в файловом сервисе;
/// третьим потребителем стал интерфейс, показывающий человеку, что значение прочитала модель.
/// </summary>
public class DataOriginTests
{
    private static DataSetSource SourceWith(string sheetOrPath)
        => DataSetFile.Create("f.pdf", DataSetFormat.Pdf, "b/p", CatalogScope.System, null)
            .AddSource("Источник", sheetOrPath, "[]", 0);

    [Theory]
    [InlineData("gost-documents")]
    [InlineData("gost-cover")]
    [InlineData("gost-titlepage")]
    [InlineData("invoice-header")]
    public void RecognitionMarkers_AreRecognized(string marker)
        => Assert.Equal(DataOrigin.Recognized, SourceWith(marker).Origin);

    [Fact]
    public void TableMarkerWithId_IsRecognized()
    {
        // Таблица документа помечается маркером с идентификатором группы — префиксом, а не точным
        // совпадением: проверка по списку целиком пропустила бы её.
        Assert.Equal(DataOrigin.Recognized, SourceWith($"{PdfProfiles.GostTableMarkerPrefix}{Guid.NewGuid()}").Origin);
    }

    [Fact]
    public void SystemMarker_IsSystem()
        => Assert.Equal(DataOrigin.System, SourceWith("system:quality-docs").Origin);

    [Theory]
    [InlineData("Лист1")]
    [InlineData("/root/items")]
    [InlineData("$.rows")]
    [InlineData("default")]
    public void EverythingElse_IsParsed(string sheetOrPath)
        => Assert.Equal(DataOrigin.Parsed, SourceWith(sheetOrPath).Origin);
}
