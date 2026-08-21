using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Устаревание данных источника: признак и его причина (issue #815).
///
/// Признак раньше был булевым и жил на ДВУХ сущностях сразу — на источнике (где его выставляли) и на
/// файле (где его только читали). Здесь закреплено то, ради чего эта раздвоенность снята: причина
/// одна, ставится в момент события и не переписывается задним числом.
/// </summary>
public class StaleReasonTests
{
    private static DataSetSource Source()
        => DataSetFile.Create("альбом.pdf", DataSetFormat.Pdf, "b/p", CatalogScope.System, null)
            .AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);

    [Fact]
    public void FreshSource_IsNotStale()
    {
        var s = Source();
        Assert.Null(s.StaleReason);
        Assert.False(s.RecognitionStale);
    }

    [Fact]
    public void Mark_SetsReason_AndFlagFollows()
    {
        var s = Source();
        s.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
        Assert.Equal(DataSetStaleReason.FileReplaced, s.StaleReason);
        Assert.True(s.RecognitionStale);
    }

    [Fact]
    public void SecondMark_KeepsWiderReason()
    {
        // Файл заменили, а потом сменили профиль: сказать надо про замену файла — она объясняет
        // расхождение полностью, а смена профиля поверх неё описывает лишь часть беды.
        var s = Source();
        s.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
        s.MarkRecognitionStale(DataSetStaleReason.ProfileChanged);
        Assert.Equal(DataSetStaleReason.FileReplaced, s.StaleReason);
    }

    [Fact]
    public void WiderReason_OverridesNarrowerOne_RegardlessOfOrder()
    {
        // Обратный порядок событий так же законен: профиль привязали вчера, файл подменили сегодня.
        // Правило «первая причина выигрывает» рассказывало бы тут про профиль — и занижало бы беду.
        var s = Source();
        s.MarkRecognitionStale(DataSetStaleReason.ProfileChanged);
        s.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
        Assert.Equal(DataSetStaleReason.FileReplaced, s.StaleReason);
    }

    [Fact]
    public void EqualWeightReasons_DoNotChurn()
    {
        // Замена файла и неразобравшийся источник обесценивают одно и то же — переписывать первую
        // причину второй незачем: сообщение человеку от этого не станет точнее.
        var s = Source();
        s.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
        s.MarkRecognitionStale(DataSetStaleReason.NotParsedAgainstNewFile);
        Assert.Equal(DataSetStaleReason.FileReplaced, s.StaleReason);
    }

    [Fact]
    public void FreshCache_ClearsReason()
    {
        var s = Source();
        s.MarkRecognitionStale(DataSetStaleReason.TableBoundariesChanged);
        s.UpdateCache("[]", 3);
        Assert.Null(s.StaleReason);
        Assert.False(s.RecognitionStale);
    }

    // ── Какие источники обесценивает смена профиля НАБОРА ────────────────────────

    [Theory]
    [InlineData("TitleBlock", PdfProfiles.GostDocumentsMarker)]
    [InlineData("TitleBlock", PdfProfiles.LegacyTitleBlockRegistryMarker)]
    [InlineData("CoverTitle", PdfProfiles.GostCoverMarker)]
    [InlineData("CoverTitle", PdfProfiles.GostTitlePageMarker)]
    [InlineData("Invoice", PdfProfiles.InvoiceHeaderMarker)]
    [InlineData("Invoice", PdfProfiles.InvoiceLineItemsMarker)]
    public void FileProfileKind_CoversItsOwnSources(string kind, string marker)
        => Assert.Contains(marker, PdfProfiles.MarkersForFileProfileKind(kind));

    [Fact]
    public void FileProfileKind_DoesNotCoverForeignSources()
    {
        // Сменили профиль штампа — обложка ни при чём: пометив её, мы отправили бы человека
        // перераспознавать то, что не менялось.
        Assert.DoesNotContain(PdfProfiles.GostCoverMarker, PdfProfiles.MarkersForFileProfileKind("TitleBlock"));
        Assert.DoesNotContain(PdfProfiles.GostDocumentsMarker, PdfProfiles.MarkersForFileProfileKind("CoverTitle"));
    }

    [Theory]
    [InlineData("Table")]
    [InlineData("CableJournal")]
    [InlineData("СовершенноНовыйВид")]
    public void UnknownOrGroupScopedKind_MarksNothing(string kind)
    {
        // Табличные виды привязываются к ГРУППЕ листов, и их источник адресуется id группы, а не
        // видом; незнакомый вид не должен молча помечать устаревшим всё подряд.
        Assert.Empty(PdfProfiles.MarkersForFileProfileKind(kind));
    }
}
