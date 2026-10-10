using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Files;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел размера скана и перечень распознаваемых видов записаны в трёх местах — модуль, ядро и экран
/// (issue #1093, ревью PR #1260). Свести их в одно нельзя: модуль не ссылается на проект API, а клиент —
/// другой язык. Поэтому совпадение сверяется: разойдись числа, экран начал бы отвергать то, что сервер
/// принимает, или пропускать то, что сервер отвергнет, — и ни одна сторона об этом не сказала бы.
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

    [Fact]
    public void Экран_принимает_те_же_виды_файла()
    {
        var line = Screen.Split('\n').Single(l => l.StartsWith("export const READABLE = ["));
        var listed = line[(line.IndexOf('[') + 1)..line.IndexOf(']')].Split(',').Select(mime => mime.Trim().Trim('\''));
        Assert.Equal(InvoiceScanRecognition.Readable.Order(), listed.Order());
    }

    /// <summary>
    /// Что показывается рядом с формой, экран решает своим перечнем (issue #1265, ревью PR #1275).
    /// Разойдись он с серверным, новый вид молча остался бы на экране «только скачать» — либо экран
    /// открыл бы то, чего сервер показываемым не называл.
    /// </summary>
    [Fact]
    public void Экран_показывает_те_же_виды_что_называет_сервер()
    {
        var view = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "client", "src", "features", "costs", "scanView.ts"));
        var line = view.Split('\n').Single(l => l.StartsWith("export const SHOWN = ["));
        var listed = line[(line.IndexOf('[') + 1)..line.IndexOf(']')].Split(',').Select(mime => mime.Trim().Trim('\''));
        Assert.Equal(FileKinds.Shown.Order(), listed.Order());
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
