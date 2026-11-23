using System.Text.RegularExpressions;
using BHS.CRG.Modules.Work.Summary;

namespace BHS.CRG.Tests.Work;

public class SummaryStateCatalogTests
{
    [Fact]
    public void Клиент_и_сервер_объявляют_одни_коды_в_одном_порядке()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        var file = Path.Combine(Assert.IsType<DirectoryInfo>(root).FullName, "src/client/src/shared/api/work.ts");
        var block = Regex.Match(File.ReadAllText(file), @"WORK_SUMMARY_STATES\s*=\s*\[(?<body>[^\]]*)\]");
        Assert.True(block.Success, "В work.ts не найден WORK_SUMMARY_STATES.");
        var client = Regex.Matches(block.Groups["body"].Value, "'([^']+)'").Select(m => m.Groups[1].Value);
        Assert.Equal(SummaryStates.All, client);
        Assert.Equal(8, SummaryStates.All.Distinct().Count());
    }
}
