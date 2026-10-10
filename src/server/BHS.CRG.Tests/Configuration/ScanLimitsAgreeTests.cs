using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел размера скана записан в двух местах — реестр видов файлов и ядро (issue #1093, ревью
/// PR #1260). Свести их в одно нельзя: контракты модулей не ссылаются на проект API. Поэтому
/// совпадение сверяется. Экран своего числа не держит вовсе: предел приходит ему с реестром
/// (ревью PR #1279), и сторож ниже следит, чтобы число не завелось там снова.
///
/// <para>Перечни видов файла здесь больше не сверяются: их источник один — реестр ядра
/// (issue #1266), а что рядом с ним не завелось второго перечня, сторожит <c>FileKindCatalogTests</c>.</para>
/// </summary>
public class ScanLimitsAgreeTests
{
    private static readonly string Screen = File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "client", "src", "features", "costs", "scanBatch.ts"));

    /// <summary>
    /// Предел модуля — предел вложения ядра: от него считается предел тела запроса, и скан больше
    /// него до нашей проверки не дошёл бы вовсе.
    /// </summary>
    [Fact]
    public void Предел_скана_равен_пределу_вложения_ядра() =>
        Assert.Equal(UploadLimits.Attachment, InvoiceScanRecognition.MaxScanBytes);

    [Fact]
    public void Экран_берёт_предел_размера_из_реестра_а_не_держит_своё_число()
    {
        Assert.Contains("kinds.maxBytes", Screen);
        Assert.DoesNotContain("1024 * 1024", Screen);
        Assert.DoesNotMatch(@"\d+ МБ", Screen);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory + ".");
    }
}
