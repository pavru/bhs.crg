using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Код модулей и проекта контрактов не обращается к таблицам ядра сырым запросом (задача M3,
/// issue #1071; ревью PR #1250).
///
/// <para><b>От чего это.</b> Переписи ядра ищут обращения к его данным по именам наборов и хранилищ:
/// <c>ActivityRecords</c>, <c>IRepository&lt;…&gt;</c>, <c>.DomainObjects</c>. Модулю всё это недоступно —
/// ссылок на слои ядра у него нет, — и в его коде такие выражения совпасть не могут в принципе. Зато
/// у модуля та же база: <c>db.Database.ExecuteSqlRawAsync("DELETE FROM domain_objects …")</c> из его
/// собственного контекста проходит. Это единственный путь модуля к данным ядра мимо портов, и он
/// обходит разом удаление с вопросом держателям, замок закрытого периода, журнал и охрану записи.</para>
///
/// <para>Поэтому правило одно на все таблицы ядра, а не по строке в каждой переписи. Имена таблиц
/// берутся из снимка модели ядра — новая таблица попадает под правило сама.</para>
///
/// <para>⚠️ Читаются и МИГРАЦИИ модуля: рукописный <c>migrationBuilder.Sql(…)</c> — тот же сырой запрос,
/// и «поправить данные ядра заодно» тянет написать именно там.</para>
///
/// <para>⚠️ <b>Чего не видит.</b> Имя таблицы, собранное не литералом, и запрос, где между словом
/// <c>FROM</c>/<c>UPDATE</c>/… и именем таблицы стоит что-то кроме схемы и кавычек.</para>
/// </summary>
public class ModuleRawSqlTests
{
    /// <summary>Кому обращение к таблице ядра разрешено — с причиной.</summary>
    private static readonly Dictionary<string, string> Deliberate = new();

    private static readonly Regex TableName = new(@"\.ToTable\(""(\w+)""", RegexOptions.Compiled);

    [Fact]
    public void Модуль_не_трогает_таблицы_ядра_сырым_запросом()
    {
        var tables = CoreTables();
        // Проверять есть чем: снимок переехал или сменил запись — и выражение ниже не совпало бы ни с чем.
        Assert.True(tables.Count >= 30 && tables.Contains("domain_objects") && tables.Contains("activity_log"),
            $"Из снимка модели ядра собрано таблиц: {tables.Count}. Среди них нет ожидаемых — проверка ниже " +
            "не смотрит ни на что.");

        var touch = new Regex(
            @"\b(?:FROM|INTO|UPDATE|JOIN|TABLE|TRUNCATE)\s+(?:ONLY\s+)?(?:public\.)?\\?""?(" +
            string.Join('|', tables.Select(Regex.Escape)) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var found = new List<string>();
        foreach (var file in Sources())
        {
            var rel = SourceTree.Relative(file);
            if (Deliberate.ContainsKey(rel)) continue;

            var text = Code(File.ReadAllText(file));
            foreach (Match m in touch.Matches(text))
                found.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}  {m.Value.Trim()}");
        }

        Assert.True(found.Count == 0,
            "Код модуля обращается к таблице ядра сырым запросом:\n" + string.Join("\n", found) + "\n\n" +
            "Данные ядра модуль читает и меняет портами (IModuleCatalog, IModuleActivityLog, IModulePeriods…): " +
            "только там действуют права, замок закрытого периода, журнал и вопрос держателям перед удалением. " +
            "Если обращение всё-таки нужно — впишите файл в Deliberate с причиной.");
    }

    [Fact]
    public void Перечень_не_хранит_умерших_записей()
    {
        var live = Sources().Select(SourceTree.Relative).ToHashSet(StringComparer.Ordinal);
        var stale = Deliberate.Keys.Where(rel => !live.Contains(rel)).ToList();

        Assert.True(stale.Count == 0,
            "В Deliberate файлы, которых больше нет: " + string.Join(", ", stale) + ".");
    }

    /// <summary>Имена таблиц ядра — из снимка модели: туда попадает каждая, включая таблицы Identity.</summary>
    private static HashSet<string> CoreTables()
    {
        var snapshot = Path.Combine(
            SourceTree.SolutionDir, "BHS.CRG.Infrastructure", "Migrations", "AppDbContextModelSnapshot.cs");
        return TableName.Matches(File.ReadAllText(snapshot))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Проект контрактов и проекты модулей — вместе с миграциями, без <c>obj</c> и <c>bin</c>.</summary>
    private static IEnumerable<string> Sources()
    {
        var sep = Path.DirectorySeparatorChar;
        return SolutionModules.Names.Prepend(SolutionModules.ContractsProject)
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(SourceTree.SolutionDir, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    /// <summary>Без строк-комментариев: «обновление из таблицы…» в пояснении — не запрос.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart().StartsWith("//") ? string.Empty : line));
}
