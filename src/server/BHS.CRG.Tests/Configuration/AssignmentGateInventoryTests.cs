using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись пишущих адресов назначений (design <c>work-11-assignments</c>, «Право по стройке»): каждый
/// <c>MapPost</c>, <c>MapPut</c> и <c>MapDelete</c> под <c>/api/work/assignments</c> обязан звать ворота стройки
/// (<c>AssignmentGate</c>). Ворота права <c>work.assignment.edit</c> стоят на группе и стройки не знают: адрес,
/// забывший ворота стройки, пустил бы менеджера стройки А на стройку Б — молча и с кодом 201.
///
/// <para>⚠️ Читает исходники, как <see cref="ActivityLogInventoryTests" />: в метаданных маршрута не видно, что
/// обработчик спросил ворота.</para>
/// </summary>
public class AssignmentGateInventoryTests
{
    private static readonly Regex Write = new(@"\.Map(Post|Put|Delete)\(""[^""]*"",\s*(\w+)\)", RegexOptions.Compiled);

    private static readonly Regex Gate = new(@"\bgate\.(EnsureAsync|RemovalAsync)\(", RegexOptions.Compiled);

    private static IEnumerable<string> Sources() =>
        SourceTree.Files("BHS.CRG.Modules.Work").Where(f => f.Replace('\\', '/').Contains("/Endpoints/Assignment"));

    [Fact]
    public void Каждый_пишущий_адрес_назначений_зовёт_ворота_стройки()
    {
        var found = new List<string>();
        var bare = new List<string>();
        foreach (var file in Sources())
        {
            var text = SourceTree.WithoutComments(File.ReadAllText(file));
            foreach (Match m in Write.Matches(text))
            {
                var handler = m.Groups[2].Value;
                found.Add(handler);
                if (!Gate.IsMatch(Body(text, handler))) bare.Add($"{SourceTree.Relative(file)}: {m.Groups[1].Value} → {handler}");
            }
        }

        // Четыре пишущих адреса: заведение, правка, снятие, пачка. Список держит перепись достижимой: обработчики,
        // переименованные так, что регулярное выражение их не видит, дали бы зелёное на пустом месте.
        Assert.Equal(["BulkAsync", "ChangeAsync", "CreateAsync", "RemoveAsync"], found.Order());
        Assert.True(bare.Count == 0,
            "Пишущие адреса назначений без ворот стройки:\n" + string.Join("\n", bare) + "\n\n" +
            "Право work.assignment.edit на группе стройку не проверяет. Позовите AssignmentGate.EnsureAsync " +
            "(или RemovalAsync у снятия) до записи.");
    }

    /// <summary>Сторож срабатывает: обработчик без ворот находится.</summary>
    [Fact]
    public void Сторож_находит_адрес_без_ворот()
    {
        const string sample = """
            group.MapPost("", CreateAsync);
            private static async Task<IResult> CreateAsync(WorkDbContext db) { db.Assignments.Add(x); }
            """;
        Assert.Single(Write.Matches(sample));
        Assert.DoesNotMatch(Gate, Body(sample, "CreateAsync"));
    }

    /// <summary>Тело обработчика — от его объявления до следующего объявления метода.</summary>
    private static string Body(string text, string handler)
    {
        var start = Regex.Match(text, $@"\bTask<IResult>\s+{handler}\(");
        if (!start.Success) return "";
        var next = Regex.Match(text[(start.Index + start.Length)..], @"\n\s*(private|internal|public)\s+static\s");
        return next.Success ? text.Substring(start.Index, start.Length + next.Index) : text[start.Index..];
    }
}
