using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class PdfDataSetParserTests
{
    private readonly PdfDataSetParser _parser = new();

    [Fact]
    public void CanParse_OnlyPdf()
    {
        Assert.True(_parser.CanParse(DataSetFormat.Pdf));
        Assert.False(_parser.CanParse(DataSetFormat.Xml));
    }

    [Fact]
    public async Task DetectSources_AlwaysReturnsEmpty()
    {
        // Как и XML — источники только вручную; для PDF ещё и потому, что автодетект
        // потребовал бы запуска (платного/небыстрого) распознавания без согласия пользователя.
        var sources = await _parser.DetectSourcesAsync([], default);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWithGuidanceToRecognizeAction()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _parser.ParseAsync([], "titleblock-registry", null, default));
        Assert.Contains("Распознать", ex.Message);
    }
}
