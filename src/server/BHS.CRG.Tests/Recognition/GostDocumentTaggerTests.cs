using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostDocumentTaggerTests
{
    [Theory]
    [InlineData("Спецификация оборудования, изделий и материалов", FunctionalTag.GostDocSpecification)]
    [InlineData("СПЕЦИФИКАЦИЯ", FunctionalTag.GostDocSpecification)]
    [InlineData("Ведомость материалов", FunctionalTag.GostDocSpecification)]
    [InlineData("Ведомость оборудования", FunctionalTag.GostDocSpecification)]
    [InlineData("Кабельный журнал", FunctionalTag.GostDocCableJournal)]
    [InlineData("кабельный журнал сети 0,4 кВ", FunctionalTag.GostDocCableJournal)]
    public void DetectTableTag_MatchesByName(string name, string expected)
        => Assert.Equal(expected, GostDocumentTagger.DetectTableTag(name));

    [Theory]
    [InlineData("1ВРУ. Схема электрическая принципиальная распределительной сети")]
    [InlineData("Общие данные")]
    [InlineData("План сетей освещения")]
    [InlineData("Ведомость чертежей")] // ведомость, но не материалов/оборудования
    [InlineData("")]
    [InlineData(null)]
    public void DetectTableTag_ReturnsNullForNonTables(string? name)
        => Assert.Null(GostDocumentTagger.DetectTableTag(name));
}
