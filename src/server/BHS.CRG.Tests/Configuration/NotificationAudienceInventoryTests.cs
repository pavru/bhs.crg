using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись издателей уведомлений: каждая публикация называет адресата — человека
/// (<c>userId</c>) или право (<c>audience</c>). Кто звонит ВСЕМ вошедшим, перечислен здесь
/// поимённо (issue #949, ТЗ AUTH-13).
///
/// Сторож нужен потому, что «всем подряд» — это не написанная строка, а ЗАБЫТАЯ: оба параметра
/// необязательны, и публикация без адресата компилируется, работает и выглядит правильно. Заметить
/// её можно только глазами того, у кого прав нет, — а смотрит обычно администратор, у которого есть
/// всё. Приём тот же, что у инвентаризации адресов (<see cref="Integration.EndpointGateInventoryTests" />):
/// связать «добавил уведомление» с «назови, кому оно», пока решение ещё дёшево.
///
/// ⚠️ Проверка читает ИСХОДНИКИ: в метаданных сборки необязательный аргумент, который не передали,
/// никак не отражается — его просто нет.
/// </summary>
public class NotificationAudienceInventoryTests
{
    private static readonly string[] Projects = ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    /// <summary>
    /// Кому дозволено звонить всем вошедшим — с причиной. Добавляя строку, вы принимаете решение:
    /// это уведомление действительно касается каждого, кто вошёл в систему.
    /// </summary>
    private static readonly Dictionary<string, string> EveryoneSignedIn = new()
    {
        ["BHS.CRG.Api/Notifications/HealthMonitorService.cs"] =
            "состояние системы и компонент: ту же панель (/api/notifications/health) открыли всем вошедшим — " +
            "решение владельца 20.09.2026. Уведомление и панель обязаны говорить одно и то же",
    };

    /// <summary>Файлы, где <c>PublishAsync</c> — объявление или фейк, а не публикация.</summary>
    private static readonly string[] NotPublishers =
    [
        "BHS.CRG.Application/Notifications/INotificationService.cs",
        "BHS.CRG.Infrastructure/Notifications/NotificationService.cs",
    ];

    // Вызов целиком, со скобками: аргументы переносят на следующие строки, и построчная проверка
    // пропустила бы всё, что не уместилось в одну.
    private static readonly Regex Call = new(@"PublishAsync\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Каждое_уведомление_называет_адресата_или_записано_в_перепись()
    {
        var nameless = new List<string>();

        foreach (var file in Projects.SelectMany(SourceFiles))
        {
            var rel = Relative(file);
            if (NotPublishers.Contains(rel) || EveryoneSignedIn.ContainsKey(rel)) continue;

            var text = File.ReadAllText(file);
            foreach (Match m in Call.Matches(text))
            {
                var args = Arguments(text, m.Index + m.Length);
                if (args.Contains("userId") || args.Contains("audience:")) continue;
                nameless.Add($"{rel}:{LineOf(text, m.Index)}");
            }
        }

        Assert.True(nameless.Count == 0,
            "Уведомление опубликовано без адресата — значит, придёт всем вошедшим, включая тех, кого " +
            "оно не касается:\n" + string.Join("\n", nameless) + "\n\n" +
            "Назовите право (audience: NotificationAudiences.…) или человека (userId:). " +
            "Если оно и правда для всех — впишите файл в EveryoneSignedIn с причиной.");
    }

    /// <summary>
    /// Запись в переписи, за которой больше нет ни одной публикации, — след переезда. Оставленная,
    /// она тихо разрешает следующему автору этого файла звонить всем.
    /// </summary>
    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = EveryoneSignedIn.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !Call.IsMatch(File.ReadAllText(path));
            })
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, где уведомлений больше нет: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись разрешает то, чего уже не делают.");
    }

    /// <summary>
    /// Аргументы вызова — от открывающей скобки до парной закрывающей. Со счётом скобок, потому что
    /// внутри есть свои: интерполяция, тернарники, вложенные вызовы. Строковые литералы пропускаем,
    /// иначе скобка в тексте сообщения сбила бы счёт.
    /// </summary>
    private static string Arguments(string text, int start)
    {
        var depth = 1;
        var i = start;
        for (; i < text.Length && depth > 0; i++)
        {
            var c = text[i];
            if (c == '"') { i = SkipString(text, i); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
        }
        return text[start..Math.Min(i, text.Length)];
    }

    /// <summary>Индекс закрывающей кавычки строки, начавшейся на <paramref name="i" />.</summary>
    private static int SkipString(string text, int i)
    {
        for (var j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '"') return j;
            if (text[j] == '\n') return j;   // не закрылась на строке — не наше дело, идём дальше
        }
        return text.Length - 1;
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Relative(string full) =>
        Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает исходники и без них проверять нечего.");
    }
}
