using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Разбор исходников для <see cref="RecordVersionInventoryTests" />: кто наследует
/// <c>ModuleRecordVersion</c> и кто обращается к их наборам (ТЗ CORE-36, change <c>plan-12-record-versions</c>).
///
/// <para>Отдельно от тестов потому, что работает он на двух входах: на исходниках решения и на образцах
/// нарушений (<c>TestData/RecordVersionInventory</c>). Самопроверка на образцах — единственное, что
/// отличает сторожа от проверки, которая зелёная всегда: на master наследников нет, и без образцов ни
/// один запрет ни разу не сработал бы.</para>
///
/// <para>⚠️ <b>Чего он не видит.</b> Набор, полученный не по имени и не по типу — из переменной типа
/// <c>IQueryable</c>, отданной другим файлом, или через рефлексию. Сторож читает текст; обращения,
/// которого в тексте нет, ему не найти.</para>
/// </summary>
public static class RecordVersionCensus
{
    public const string BaseType = "ModuleRecordVersion";

    /// <summary>Файл, как его читает сторож: путь от каталога решения и текст без строк-комментариев.</summary>
    public sealed record Source(string Rel, string Text)
    {
        public static Source Read(string full, string rel) =>
            new(rel, string.Join("\n", File.ReadAllLines(full).Select(l => l.TrimStart().StartsWith("//") ? "" : l)));
    }

    /// <summary>Нарушение: код запрета, тип версии (если он есть) и место.</summary>
    public sealed record Violation(string Code, string Type, string Rel, int Line)
    {
        public override string ToString() => $"[{Code}] {Type}: {Rel}:{Line}";
    }

    /// <summary>Наследник базового типа: имя, файл объявления, таблица и шаблон обращения к его набору.</summary>
    public sealed record Heir(string Type, string DeclaredIn, string? Table, Regex Access);

    /// <summary>Что сторожу разрешено и почему — словари переписи (у образцов — свои).</summary>
    public sealed record Rules(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MayTouch,
        IReadOnlyDictionary<string, string> TotalsFiles,
        IReadOnlyDictionary<string, string> ShapeExceptions);

    public const string NoDictionary = "нет словаря";
    public const string Outsider = "мимо словаря";
    public const string Bypass = "мимо отслеживания";
    public const string Table = "таблица по имени";
    public const string Totals = "итоги читают историю";
    public const string Shape = "форма версии мимо базового типа";

    /// <summary>Файлы итогов по имени. Прочие итоги названы в словаре переписи.</summary>
    private static readonly Regex TotalsName = new(@"(Views|Totals|ClosingReport)\.cs$", RegexOptions.Compiled);

    private static readonly Regex HeirDeclaration = new(
        $@"\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*{BaseType}\b", RegexOptions.Compiled);

    private static readonly Regex AppendOnlyClass = new(
        @"\bclass\s+(\w+)[^{;]*?:\s*[^{;]*\bIAppendOnly\b", RegexOptions.Compiled);

    private static readonly Regex AnyClass = new(@"\bclass\s+\w+", RegexOptions.Compiled);

    private static readonly Regex Untracked = new(
        @"\.(ExecuteUpdate|ExecuteDelete)(Async)?\b|\b(FromSql|ExecuteSql|SqlQuery)\w*\b", RegexOptions.Compiled);

    public static IReadOnlyList<Heir> Heirs(IReadOnlyList<Source> files)
    {
        var heirs = new List<Heir>();
        foreach (var file in files)
        foreach (Match m in HeirDeclaration.Matches(file.Text))
        {
            var type = m.Groups[1].Value;
            var t = Regex.Escape(type);

            var table = files.Select(f => Regex.Match(f.Text, $@"\bMap<{t}>\(\s*\w+\s*,\s*""(\w+)""")).FirstOrDefault(x => x.Success)
                        ?? Regex.Match(file.Text, @"\.ToTable\(\s*""(\w+)""");
            var sets = files.SelectMany(f => Regex.Matches(f.Text, $@"\bDbSet<{t}>\s+(\w+)").Select(x => x.Groups[1].Value))
                .Distinct().Select(Regex.Escape);

            var access = string.Join("|", new[] { $@"\bSet<{t}>", $@"\b{t}\.Create\(", $@"\bnew\s+{t}\s*\(" }
                .Concat(sets.Select(s => $@"\b{s}\b")));
            heirs.Add(new Heir(type, file.Rel, table.Success ? table.Groups[1].Value : null,
                new Regex(access, RegexOptions.Compiled)));
        }

        return heirs;
    }

    public static List<Violation> Scan(IReadOnlyList<Source> files, Rules rules)
    {
        var heirs = Heirs(files);
        var found = new List<Violation>();

        foreach (var heir in heirs)
        {
            if (!rules.MayTouch.TryGetValue(heir.Type, out var mayTouch))
            {
                found.Add(new Violation(NoDictionary, heir.Type, heir.DeclaredIn, 1));
                mayTouch = new Dictionary<string, string>();
            }

            foreach (var file in files)
            {
                ScanAccess(file, heir, mayTouch, rules, found);

                if (heir.Table is not null && file.Rel != heir.DeclaredIn)
                    foreach (Match m in Regex.Matches(file.Text, $@"\b{Regex.Escape(heir.Table)}\b"))
                        found.Add(new Violation(Table, heir.Type, file.Rel, SourceTree.LineOf(file.Text, m.Index)));
            }
        }

        foreach (var file in files)
            ScanShape(file, rules, found);

        return found;
    }

    /// <summary>Обращения к набору наследника в одном файле: мимо словаря, из итогов, мимо отслеживания.</summary>
    private static void ScanAccess(
        Source file, Heir heir, IReadOnlyDictionary<string, string> mayTouch, Rules rules, List<Violation> found)
    {
        var allowed = file.Rel == heir.DeclaredIn || mayTouch.ContainsKey(file.Rel);
        var totals = TotalsName.IsMatch(file.Rel) || rules.TotalsFiles.ContainsKey(file.Rel);

        // Оператор — от «;» до «;»: цепочка запроса с ExecuteDelete в конце — одно выражение.
        var start = 0;
        foreach (var statement in file.Text.Split(';'))
        {
            var hit = heir.Access.Match(statement);
            if (hit.Success)
            {
                var line = SourceTree.LineOf(file.Text, start + hit.Index);
                if (totals) found.Add(new Violation(Totals, heir.Type, file.Rel, line));
                else if (!allowed) found.Add(new Violation(Outsider, heir.Type, file.Rel, line));

                // Разрешение в словаре мимо отслеживания не пускает: словарь называет, КТО пишет, а не КАК.
                if (Untracked.IsMatch(statement)) found.Add(new Violation(Bypass, heir.Type, file.Rel, line));
            }

            start += statement.Length + 1;
        }
    }

    /// <summary>Дописываемый класс с номером и содержимым, не наследующий базовый тип, — версия мимо него.</summary>
    private static void ScanShape(Source file, Rules rules, List<Violation> found)
    {
        foreach (Match m in AppendOnlyClass.Matches(file.Text))
        {
            var type = m.Groups[1].Value;
            if (type == BaseType || rules.ShapeExceptions.ContainsKey(type)) continue;
            if (HeirDeclaration.IsMatch(m.Value)) continue;

            var next = AnyClass.Match(file.Text, m.Index + m.Length);
            var body = file.Text[m.Index..(next.Success ? next.Index : file.Text.Length)];
            if (Regex.IsMatch(body, @"\bint\s+Number\s*\{") && Regex.IsMatch(body, @"\bstring\s+Content\s*\{"))
                found.Add(new Violation(Shape, type, file.Rel, SourceTree.LineOf(file.Text, m.Index)));
        }
    }

    /// <summary>Классы с формой версии — для проверки, что исключение ещё кого-то называет.</summary>
    public static IEnumerable<string> ShapeClasses(IReadOnlyList<Source> files) =>
        files.SelectMany(f => AppendOnlyClass.Matches(f.Text).Select(m => m.Groups[1].Value));
}
