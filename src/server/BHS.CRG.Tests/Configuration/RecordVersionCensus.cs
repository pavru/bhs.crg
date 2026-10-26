using System.Text;
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

    /// <summary>Файл, как его читает сторож: путь от каталога решения и текст без комментариев.</summary>
    public sealed record Source(string Rel, string Text)
    {
        public static Source Read(string full, string rel) => new(rel, WithoutComments(File.ReadAllText(full)));
    }

    /// <summary>Нарушение: код запрета, тип версии (если он есть) и место.</summary>
    public sealed record Violation(string Code, string Type, string Rel, int Line)
    {
        public override string ToString() => $"[{Code}] {Type}: {Rel}:{Line}";
    }

    /// <summary>
    /// Наследник базового типа: имя, файл объявления, таблица и файл, где она названа вызовом
    /// <c>Map</c>, и шаблон обращения к его набору.
    /// </summary>
    public sealed record Heir(string Type, string DeclaredIn, string? Table, string? MappedIn, Regex Access);

    /// <summary>Что сторожу разрешено и почему — словари переписи (у образцов — свои).</summary>
    public sealed record Rules(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MayTouch,
        IReadOnlyDictionary<string, string> TotalsFiles,
        IReadOnlyDictionary<string, string> ShapeExceptions,
        IReadOnlyDictionary<string, string> TableNames);

    public const string NoDictionary = "нет словаря";
    public const string NoMap = "отображение мимо Map";
    public const string Outsider = "мимо словаря";
    public const string Bypass = "мимо отслеживания";
    public const string Table = "таблица по имени";
    public const string Totals = "итоги читают историю";
    public const string Shape = "форма версии мимо базового типа";

    /// <summary>
    /// Файлы итогов по имени. <c>*Views.cs</c> сюда не входит: так зовутся и сборщики представлений
    /// (<c>EstimateViews.cs</c>), и итоги (<c>CostViews.cs</c>) — итоги из них названы в словаре переписи.
    /// </summary>
    private static readonly Regex TotalsName = new(@"(Totals|ClosingReport)\.cs$", RegexOptions.Compiled);

    /// <summary>
    /// Объявление класса и его первого базового типа — именем, в том числе полным
    /// (<c>Data.ModuleRecordVersion</c>). Наследник ищется по цепочке: через промежуточный класс тоже.
    /// </summary>
    private static readonly Regex ClassBase = new(
        @"\b(?<abstract>abstract\s+(?:\w+\s+)*)?class\s+(?<type>\w+)\s*(?:<[^>{]*>)?\s*(?:\([^)]*\))?\s*:\s*(?:[\w.]+\.)?(?<base>\w+)\b",
        RegexOptions.Compiled);

    private static readonly Regex AppendOnlyClass = new(
        @"\bclass\s+(\w+)[^{;]*?:\s*[^{;]*\bIAppendOnly\b", RegexOptions.Compiled);

    private static readonly Regex AnyClass = new(@"\bclass\s+\w+", RegexOptions.Compiled);

    private static readonly Regex Untracked = new(
        @"\.(ExecuteUpdate|ExecuteDelete)(Async)?\b|\b(FromSql|ExecuteSql|SqlQuery)\w*\b", RegexOptions.Compiled);

    public static IReadOnlyList<Heir> Heirs(IReadOnlyList<Source> files)
    {
        var classes = files.SelectMany(f => ClassBase.Matches(f.Text).Select(m => (
                Type: m.Groups["type"].Value, Base: m.Groups["base"].Value, Abstract: m.Groups["abstract"].Success, f.Rel)))
            .ToList();

        // Цепочка наследования: промежуточный класс — тоже основа, но набора у него нет.
        var bases = new HashSet<string> { BaseType };
        while (classes.Where(c => bases.Contains(c.Base)).Select(c => c.Type).Where(bases.Add).ToList().Count > 0) { }

        var heirs = new List<Heir>();
        foreach (var (type, _, _, declaredIn) in classes.Where(c => bases.Contains(c.Base) && !c.Abstract))
        {
            var t = Regex.Escape(type);

            // Таблицу называет только Map — и только строкой: иначе её имени сторожу не прочесть.
            var map = files.Select(f => (f.Rel, Match: Regex.Match(f.Text, $@"\bMap<\s*{t}\s*,[^>]*>\(\s*\w+\s*,\s*""(\w+)""")))
                .FirstOrDefault(x => x.Match.Success);
            var sets = files.SelectMany(f => Regex.Matches(f.Text, $@"\bDbSet<{t}>\s+(\w+)").Select(x => x.Groups[1].Value))
                .Distinct().Select(Regex.Escape);

            var access = string.Join("|", new[] { $@"\bSet<{t}>", $@"\b{t}\.Create\(", $@"\bnew\s+{t}\s*\(" }
                .Concat(sets.Select(s => $@"\b{s}\b")));
            heirs.Add(new Heir(type, declaredIn, map.Match?.Groups[1].Value, map.Rel,
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

            // Без Map нет ни индекса номера, ни связи с Restrict — и таблицы, по которой искать сырой SQL.
            if (heir.Table is null) found.Add(new Violation(NoMap, heir.Type, heir.DeclaredIn, 1));

            foreach (var file in files)
            {
                ScanAccess(file, heir, mayTouch, rules, found);

                if (heir.Table is not null && file.Rel != heir.DeclaredIn && file.Rel != heir.MappedIn
                    && !rules.TableNames.ContainsKey(file.Rel))
                    foreach (Match m in Regex.Matches(file.Text, $@"\b{Regex.Escape(heir.Table)}\b"))
                        found.Add(new Violation(Table, heir.Type, file.Rel, SourceTree.LineOf(file.Text, m.Index)));
            }
        }

        foreach (var file in files)
        foreach (var (type, line) in ShapeMatches(file).Where(s => !rules.ShapeExceptions.ContainsKey(s.Type)))
            found.Add(new Violation(Shape, type, file.Rel, line));

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

    /// <summary>
    /// Дописываемые классы файла с формой версии — номером и содержимым — мимо базового типа. Исключения
    /// переписи здесь не учтены: по этому же перечню проверяется, что исключение ещё кого-то называет.
    /// </summary>
    private static IEnumerable<(string Type, int Line)> ShapeMatches(Source file)
    {
        foreach (Match m in AppendOnlyClass.Matches(file.Text))
        {
            var type = m.Groups[1].Value;
            if (type == BaseType) continue;

            var next = AnyClass.Match(file.Text, m.Index + m.Length);
            var body = file.Text[m.Index..(next.Success ? next.Index : file.Text.Length)];
            if (Regex.IsMatch(body, @"\bint\s+Number\s*\{") && Regex.IsMatch(body, @"\bstring\s+Content\s*\{"))
                yield return (type, SourceTree.LineOf(file.Text, m.Index));
        }
    }

    /// <summary>Типы с формой версии мимо базового — для проверки, что исключение ещё кого-то называет.</summary>
    public static IEnumerable<string> ShapeClasses(IReadOnlyList<Source> files) =>
        files.SelectMany(ShapeMatches).Select(s => s.Type);

    /// <summary>
    /// Текст без комментариев — строчных (и в конце строки кода), блочных и документирующих; переводы строк
    /// остаются, чтобы номер строки нарушения совпал с файлом. Строки и символы читаются как есть —
    /// <c>"https://…"</c> не комментарий: обычная, дословная (<c>@"…"</c>) и сырая (<c>"""…"""</c>).
    /// </summary>
    public static string WithoutComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                result.Append('\n', text.AsSpan(i, stop - i).Count('\n'));
                i = stop;
                continue;
            }

            if (c == '"' && text.AsSpan(i).StartsWith("\"\"\""))
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 3;
                result.Append(text, i, stop - i);
                i = stop;
                continue;
            }

            if (c is '"' or '\'')
            {
                var verbatim = c == '"' && i > 0 && text[i - 1] == '@';
                var j = i + 1;
                while (j < text.Length)
                {
                    if (verbatim && text[j] == '"' && j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; }
                    if (!verbatim && text[j] == '\\') { j += 2; continue; }
                    if (text[j] == c || (!verbatim && text[j] == '\n')) break;
                    j++;
                }

                var stop = Math.Min(j + 1, text.Length);
                result.Append(text, i, stop - i);
                i = stop;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }
}
