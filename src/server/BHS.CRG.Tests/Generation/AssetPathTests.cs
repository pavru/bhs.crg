using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Форма пути к ассету (issue #513). Строка прибита тестом намеренно: её же — с ведущим «/» и той же
/// подпапкой — строит клиент в <c>templateAssetRef.ts</c>, чтобы вставлять в редактор при выборе
/// ассета. Через границу процессов константу не разделить, поэтому обе стороны держатся тестами на
/// точную строку: разъедутся — упадёт один из них, а не пользовательский шаблон.
/// </summary>
public class AssetPathTests
{
    [Fact]
    public void PathIsRootAbsolute()
        => Assert.Equal("/assets/img_0.png", AssetPath.FromRoot("assets", "img_0.png"));

    /// <summary>Двойного слэша быть не должно — путь идёт прямо в Typst как есть.</summary>
    [Theory]
    [InlineData("assets")]
    [InlineData("/assets")]
    [InlineData("assets/")]
    public void SlashesAroundSubdirDoNotDouble(string subdir)
        => Assert.Equal("/assets/att_1.pdf", AssetPath.FromRoot(subdir, "att_1.pdf"));
}
