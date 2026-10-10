using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел размера скана записан в трёх местах — модуль, ядро и экран (issue #1093, ревью PR #1260).
/// Свести их в одно нельзя: модуль не ссылается на проект API, а клиент — другой язык. Поэтому
/// совпадение сверяется: разойдись числа, экран начал бы отвергать то, что сервер принимает, или
/// пропускать то, что сервер отвергнет, — и ни одна сторона об этом не сказала бы.
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
    public void Экран_называет_тот_же_предел_размера()
    {
        var megabytes = InvoiceScanRecognition.MaxScanBytes / (1024 * 1024);
        Assert.Contains($"export const MAX_BYTES = {megabytes} * 1024 * 1024;", Screen);
        Assert.Contains($"'больше {megabytes} МБ'", Screen);
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
