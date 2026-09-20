using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к журналу действий: набор записей трогает ОДНА служба (issue #950, ТЗ CORE-28).
///
/// Почему сторож, а не приватные сеттеры у сущности. Проверки на уровне сущности мало: прямой
/// запрос к базе обходит и сеттеры, и фабрику — <c>db.ActivityRecords.ExecuteUpdate(...)</c>
/// компилируется, работает и меняет свидетельство. Поймать такое можно только перечислением мест,
/// где набор вообще упоминается, и перечислять обязана машина.
///
/// Сторож заодно держит и границу «журнал один на продукт»: как только запись появится вторым
/// путём — из модуля, из фоновой задачи, из восстановления копии, — она пойдёт с другим
/// представлением об авторе и времени, а сводить два журнала потом уже не с чем.
///
/// ⚠️ Проверка читает ИСХОДНИКИ: в метаданных сборки «кто обращался к набору» не отражено никак.
/// Приём тот же, что у <see cref="NotificationAudienceInventoryTests" /> и
/// <see cref="Integration.EndpointGateInventoryTests" />.
/// </summary>
public class ActivityLogInventoryTests
{
    private static readonly string[] Projects = ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    /// <summary>
    /// Кому позволено обращаться к набору записей — и почему. Добавляя строку, вы принимаете
    /// решение: этот код пишет или читает журнал В ОБХОД службы, и так и задумано.
    /// </summary>
    private static readonly Dictionary<string, string> MayTouchTheSet = new()
    {
        ["BHS.CRG.Infrastructure/Activity/ActivityLog.cs"] =
            "сама служба журнала — единственный путь записи и чтения (ТЗ CORE-28)",
        ["BHS.CRG.Infrastructure/Persistence/AppDbContext.cs"] =
            "объявление набора и отказ на правку/удаление записи: там же, где единственная точка сохранения",
    };

    /// <summary>Имя набора в контексте базы. Упоминание — это и запись, и чтение: и то и другое мимо службы.</summary>
    private static readonly Regex Set = new(@"\bActivityRecords\b", RegexOptions.Compiled);

    [Fact]
    public void К_набору_журнала_обращается_только_его_служба()
    {
        var outsiders = new List<string>();

        foreach (var file in Projects.SelectMany(SourceFiles))
        {
            var rel = Relative(file);
            if (MayTouchTheSet.ContainsKey(rel)) continue;

            var text = File.ReadAllText(file);
            foreach (Match m in Set.Matches(text))
                outsiders.Add($"{rel}:{LineOf(text, m.Index)}");
        }

        Assert.True(outsiders.Count == 0,
            "К набору записей журнала обратились мимо службы:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Журнал пишется и читается через IActivityLog: только там у записи есть автор, время и " +
            "код действия из каталога, и только там правка записи невозможна. Если обращение " +
            "всё-таки нужно — впишите файл в MayTouchTheSet с причиной.");
    }

    /// <summary>
    /// Запись в переписи, за которой больше нет обращений, — след переезда. Оставленная, она тихо
    /// разрешает следующему автору этого файла писать в журнал напрямую.
    /// </summary>
    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheSet.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !Set.IsMatch(File.ReadAllText(path));
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, где обращений к журналу больше нет: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись разрешает то, чего уже не делают.");
    }

    /// <summary>
    /// Записи журнала неизменяемы и в коде: ни одного открытого сеттера у свойств сущности.
    ///
    /// Проверка отдельная от отказа в <c>SaveChanges</c> нарочно. Отказ ловит попытку сохранить
    /// правку — то есть уже написанный код; открытый сеттер приглашает её написать, и заметен он
    /// только тому, кто пойдёт читать сущность.
    /// </summary>
    [Fact]
    public void У_записи_журнала_нет_открытых_сеттеров()
    {
        var open = typeof(BHS.CRG.Domain.Activity.ActivityRecord)
            .GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.True(open.Count == 0,
            "У записи журнала появились открытые сеттеры: " + string.Join(", ", open) +
            ". Запись только создаётся (ActivityRecord.Create) и больше не меняется (ТЗ CORE-28).");
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
