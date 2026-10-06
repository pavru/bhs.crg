using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись мест, где общий путь ЗАВОДИТ объект общей таблицы: каждое либо спрашивает, лежат ли
/// объекты этого типа в общей таблице (<c>TypeStorageRules</c>), либо названо исключением с
/// причиной (issue #1215, ревью PR #1233).
///
/// <para>Запрет «объект типа, который модуль держит в своей таблице, общим путём не заводится»
/// стоял у двух входов из пяти. Копия документа и профиль уровня заводили объект, не спрашивая, —
/// та же ошибка «закрыл один вход из нескольких», что с охраной записи (#957).</para>
///
/// <para>По исходникам и нарочно грубо: вызов правила ищется в нескольких строках ВЫШЕ места
/// создания. Что отказ действительно приходит, проверяют тесты поведения
/// (<c>ClosedTypeCommonPathTests</c>, <c>TypeOwnershipTests</c>); здесь — что о новом месте создания
/// нельзя забыть.</para>
/// </summary>
public class ClosedTypeCreationInventoryTests
{
    private static readonly string[] Projects = ["BHS.CRG.Application", "BHS.CRG.Api", "BHS.CRG.Infrastructure"];

    private static readonly Regex Creation = new(
        @"DomainObject\.(Create|CloneAsDocument|Restore|RestoreDocument)\(", RegexOptions.Compiled);

    /// <summary>Сколько строк выше места создания ищется вызов правила.</summary>
    private const int Lookback = 25;

    private const string Asks = "спрашивает";

    /// <summary>Ключ — «файл|строка кода»; значение — <see cref="Asks" /> либо причина, почему не спрашивает.</summary>
    private static readonly Dictionary<string, string> Sites = new(StringComparer.Ordinal)
    {
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);"] = Asks,
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var obj = DomainObject.Create(cmd.DocumentTypeId, null, JsonDocument.Parse(\"{}\"),"] = Asks,
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var clone = DomainObject.CloneAsDocument(source, setId, data, $\"Копия {baseName}\");"] = Asks,
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var clone = DomainObject.CloneAsDocument(source, targetSet.Id, data, baseName);"] = Asks,
        ["BHS.CRG.Infrastructure/Generation/LevelProfileService.cs|profile = DomainObject.Create(typeId.Value, null, JsonDocument.Parse(\"{}\"), level, containerId);"] = Asks,

        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.CommonData.cs|var entity = DomainObject.Restore("] =
            "восстановление копии кладёт то, что в копии лежит: копия этой системы таких объектов не " +
            "содержит. Копию, собранную не ею, запрет здесь не останавливает — названо в issue #1215",
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.cs|var obj = DomainObject.RestoreDocument("] =
            "то же — документы комплекта из копии",
    };

    [Fact]
    public void Каждое_место_заведения_объекта_названо_и_рассуждено()
    {
        var found = Find();

        var undeclared = found.Keys.Where(k => !Sites.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появилось место, где заводится объект общей таблицы, а о запрете для типов с записями в " +
            "таблице модуля оно не знает:\n" + string.Join("\n", undeclared) +
            "\n\nПозовите TypeStorageRules.EnsureCommonPathAllowed (или KeptInCommonTable) выше места " +
            "создания и впишите строку в Sites — либо впишите причину, почему запрет здесь не нужен.");

        var stale = Sites.Keys.Where(k => !found.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи места, которых в коде больше нет:\n" + string.Join("\n", stale) +
            "\nУберите строки — иначе перепись описывает несуществующее.");
    }

    [Fact]
    public void Названное_спрашивающим_действительно_спрашивает_а_исключение_нет()
    {
        var found = Find();
        var wrong = new List<string>();
        foreach (var (key, verdict) in Sites)
        {
            if (!found.TryGetValue(key, out var asksNearby)) continue; // о пропаже говорит соседний тест
            if (verdict == Asks && !asksNearby)
                wrong.Add($"{key}\n    назван спрашивающим, но вызова TypeStorageRules выше нет");
            if (verdict != Asks && asksNearby)
                wrong.Add($"{key}\n    назван исключением ({verdict}), а правило рядом стоит — решение изменилось?");
        }
        Assert.True(wrong.Count == 0, "Перепись разошлась с кодом:\n" + string.Join("\n", wrong));
    }

    /// <summary>Место создания → стоит ли выше вызов правила.</summary>
    private static Dictionary<string, bool> Find()
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in Projects.SelectMany(SourceFiles))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || !Creation.IsMatch(lines[i])) continue;
                var asks = false;
                for (var back = Math.Max(0, i - Lookback); back < i; back++)
                    if (!lines[back].TrimStart().StartsWith("//") && lines[back].Contains("TypeStorageRules.")) asks = true;
                found[$"{Relative(file)}|{lines[i].Trim()}"] = asks;
            }
        }
        return found;
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Relative(string full) =>
        Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Не найден каталог решения (BHS.CRG.slnx).");
    }
}
