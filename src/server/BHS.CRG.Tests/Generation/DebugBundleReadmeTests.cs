using BHS.CRG.Application.Generation;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// README отладочного пакета — только когда есть что сказать (change core-generation-input-one-path,
/// D5): ключ <c>--font-path</c>, если шрифт записан, и причины, по которым генерация откажет.
/// </summary>
public class DebugBundleReadmeTests
{
    private static readonly ResolutionDiagnostic Missing =
        new(DiagnosticSeverity.Error, "Номер", "Обязательное поле «Номер» не заполнено.", "missing-required");
    private static readonly ResolutionDiagnostic Mismatch =
        new(DiagnosticSeverity.Warning, "Количество", "Не число.", "value-type");

    [Fact]
    public void NothingToSay_NoReadme()
        => Assert.Null(DebugBundleArchive.Readme(new CompileLayout(null, [], []), new BundleNotes([Mismatch])));

    [Fact]
    public void FontWritten_OffersFontPath_WithoutRefusals()
    {
        var readme = DebugBundleArchive.Readme(new CompileLayout("/tmp/x/fonts", [], []), BundleNotes.None)!;

        Assert.Contains("--font-path fonts", readme);
        Assert.DoesNotContain(DebugBundleArchive.RefusalHeading, readme);
    }

    /// <summary>Ошибки и непрочитанные картинки — перечнем; предупреждения выпуск не останавливают.</summary>
    [Fact]
    public void Refusals_AreListed_WarningsAreNot()
    {
        var readme = DebugBundleArchive.Readme(new CompileLayout(null, ["Подпись"], []), new BundleNotes([Missing, Mismatch]))!;

        Assert.Contains(DebugBundleArchive.RefusalHeading, readme);
        Assert.Contains("[missing-required] Номер", readme);
        Assert.Contains("[unreadable-image] Подпись", readme);
        Assert.DoesNotContain("value-type", readme);
        Assert.DoesNotContain("--font-path", readme);
    }

    /// <summary>
    /// Ассет с именем не по правилу — тоже причина отказа, и README пишется ради неё одной (change
    /// core-template-asset-names, D3).
    /// </summary>
    [Fact]
    public void InvalidAssetName_IsListed_EvenAlone()
    {
        var readme = DebugBundleArchive.Readme(new CompileLayout(null, [], ["схема|1"]), BundleNotes.None)!;

        Assert.Contains(DebugBundleArchive.RefusalHeading, readme);
        Assert.Contains("[invalid-asset-name] схема|1", readme);
    }
}
