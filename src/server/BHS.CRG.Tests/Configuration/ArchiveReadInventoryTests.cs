using System.Reflection;
using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись ВСЕХ мест, где читают записи общей таблицы объектов, — и у каждого решение, что оно
/// делает с архивной записью (issue #1185, ТЗ CORE-34.4).
///
/// <para>Архивная запись «из выбора убрана, сохранённые ссылки целы». Мест чтения больше сотни, и
/// правило нарушается не там, где о нём подумали, а там, где забыли: один список, отдавший архивную
/// запись на выбор, выглядит исправным ровно так же, как все остальные. Поэтому новое место чтения
/// упирается в красный тест — и автор решает, пока решение дёшево.</para>
///
/// <para><b>Ключ — «файл|строка кода», а не файл.</b> Так стережётся путь: второй запрос в уже
/// названном файле — новая строка, которой в переписи нет. Одинаковые строки одного файла сливаются
/// (номера строк сдвигает любая правка выше), поэтому решение у них одно, а ЧИСЛО их названо: второй
/// такой же запрос в том же файле меняет число — и перепись краснеет (ревью PR #1224).</para>
///
/// <para><b>Перепись доказывает, что решение ПРИНЯТО, а не что оно исполнено.</b> Второй слой —
/// <see cref="ArchiveReadPurposeTests" />: у каждой строки «выбор» назван живой тест, который кладёт
/// запись в архив и смотрит, что в выборе её нет. Строка «выбор» без такого теста — отказ.</para>
///
/// <para>Чего перепись НЕ видит — честно. Вызов, перенесённый на следующую строку (<c>await repo</c>
/// / <c>.FindAsync(</c>). Вызов порта, объявленного в БАЗОВОМ классе: имя порта перепись несёт между
/// частями partial-типа (см. <see cref="Visible" />), но не по наследованию. Чтение через обобщённый
/// код, где тип объекта в файле не назван. Условие отбора на СОСЕДНЕЙ строке: убери из запроса
/// «только документы» — ключ не изменится. И клиент, попросивший «показ» для своего выбора. Первые
/// четыре ловят живые тесты там, где они есть; последнее — только правило записи (новая ссылка на
/// архивную запись — отказ), оно приезжает последним шагом.</para>
/// </summary>
public partial class ArchiveReadInventoryTests
{
    private static readonly string[] Projects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure", "BHS.CRG.Modules.Costs"];

    /// <summary>
    /// Шаги задачи #1185, которые ещё не сделаны. Отложить решение можно только на них. Сейчас
    /// пусто: шаг 4 закрыл последние отложенные чтения, а шаг 5 — правило ЗАПИСИ, мест чтения у
    /// него нет. Новое место чтения отложить больше не на что — его решают сразу.
    /// </summary>
    private static readonly int[] OpenSteps = [];

    private enum Kind
    {
        /// <summary>Выбор: архивных записей в ответе нет.</summary>
        Choice,
        /// <summary>Отбор решает звавший, назвав назначение; место обязано его исполнить.</summary>
        ByPurpose,
        /// <summary>Показ: архивные на месте, признак в ответе.</summary>
        Shown,
        /// <summary>Показ: архивные на месте, признак здесь не нужен — причина названа.</summary>
        Seen,
        /// <summary>Только документы: у документа архива нет.</summary>
        Documents,
        /// <summary>Обслуживание: копия, починка, уборка — работают со всеми записями.</summary>
        Service,
        /// <summary>Решение отложено на названный шаг этой же задачи.</summary>
        Pending,
    }

    private sealed record Row(Kind Kind, string Why, string[] Probes, int Step = 0, int Times = 1)
    {
        /// <summary>Сколько раз эта строка кода стоит в файле.</summary>
        public Row X(int times) => this with { Times = times };
    }

    private static Row Choice(string why, params string[] probes) => new(Kind.Choice, why, probes);
    private static Row ByPurpose(string why, params string[] probes) => new(Kind.ByPurpose, why, probes);
    private static Row Shown(string why) => new(Kind.Shown, why, []);
    private static Row Seen(string why) => new(Kind.Seen, why, []);
    private static Row Documents(string why) => new(Kind.Documents, why, []);
    private static Row Service(string why) => new(Kind.Service, why, []);
    private static Row Pending(int step, string why) => new(Kind.Pending, why, [], step);

    // ── Что считается чтением ─────────────────────────────────────────────────

    /// <summary>Имя, под которым репозиторий объектов объявлен в файле.</summary>
    private static readonly Regex Declared = new(
        @"(?:IRepository<DomainObject>|IDomainObjectRepository)\s+(\w+)", RegexOptions.Compiled);

    private const string NotWrite = @"(?!\.(?:Add|AddRange|Remove|RemoveRange|Update)\()";

    /// <summary>Таблица объектов напрямую: набор EF либо её имя в сыром SQL.</summary>
    private static readonly Regex Direct = new(
        @"\.DomainObjects\b" + NotWrite + @"|Set<DomainObject>\(\)" + NotWrite + @"|\bdomain_objects\b",
        RegexOptions.Compiled);

    /// <summary>Запросы MediatR, отдающие записи общих данных.</summary>
    private static readonly Regex Query = new(
        "new (?:ListCommonDataEntriesQuery|ResolveCommonDataForSetQuery|ResolveCommonDataForScopeQuery|" +
        @"SearchCommonDataForChoiceQuery|CommonDataRefsByIdsQuery|GetCommonDataEntryQuery)\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Имя, под которым порт справочников объявлен в файле. По ТИПУ, а не по слову «catalog»: порт,
    /// внедрённый под другим именем, иначе прошёл бы мимо переписи.
    /// </summary>
    private static readonly Regex DeclaredCatalog = new(@"IModuleCatalog\s+(\w+)", RegexOptions.Compiled);

    /// <summary>Состояние ссылки модуля на запись.</summary>
    private static readonly Regex States = new(@"\.StatesAsync\(ReferenceTarget\.Record", RegexOptions.Compiled);

    /// <summary>Исходный файл: проект, путь от каталога решения и текст.</summary>
    private sealed record Source(string Project, string Path, string Text);

    private static readonly Regex Namespace = new(@"^\s*namespace\s+([\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PartialType = new(
        @"\bpartial\s+(?:class|struct|interface|record(?:\s+(?:class|struct))?)\s+(\w+)", RegexOptions.Compiled);

    /// <summary>Partial-типы, части которых лежат в файле: «проект|пространство имён|имя».</summary>
    private static List<string> PartialTypes(Source source)
    {
        var ns = Namespace.Match(source.Text).Groups[1].Value;
        return PartialType.Matches(source.Text).Select(m => $"{source.Project}|{ns}|{m.Groups[1].Value}").Distinct().ToList();
    }

    /// <summary>
    /// Имена, под которыми порт виден в каждом файле: объявленные в нём самом и в ОСТАЛЬНЫХ ЧАСТЯХ его
    /// partial-типов. У такого типа порт объявлен один раз (первичный конструктор), а звать его можно
    /// из любой части — и вызов из части без объявления проходил мимо переписи молча (PR #1240:
    /// чтение, перенесённое в соседний файл класса, перестало находиться, и сторож сказал лишь
    /// «строки больше нет»).
    ///
    /// <para>По всему проекту имена не собираем нарочно: «catalog» и «repository» зовут и порты других
    /// типов, и перепись утонула бы в чужих вызовах.</para>
    /// </summary>
    private static Dictionary<string, HashSet<string>> Visible(IReadOnlyList<Source> sources, Regex declared)
    {
        var own = sources.ToDictionary(s => s.Path,
            s => declared.Matches(s.Text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal));

        var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var type in PartialTypes(source))
            {
                if (!byType.TryGetValue(type, out var names)) byType[type] = names = new(StringComparer.Ordinal);
                names.UnionWith(own[source.Path]);
            }

        foreach (var source in sources)
            foreach (var type in PartialTypes(source))
                own[source.Path].UnionWith(byType[type]);
        return own;
    }

    private static Regex? Calls(HashSet<string> names, string methods) =>
        names.Count == 0
            ? null
            : new Regex(@"\b(?:" + string.Join('|', names.Select(Regex.Escape)) + @")\.(?:" + methods + @")\(");

    private static List<Source> Sources() =>
        Projects.SelectMany(project => SourceTree.Files(project)
            .Select(file => new Source(project, SourceTree.Relative(file), File.ReadAllText(file)))).ToList();

    /// <summary>Место чтения → сколько раз такая строка стоит в файле.</summary>
    private static Dictionary<string, int> FindReads(IReadOnlyList<Source> sources)
    {
        var repositories = Visible(sources, Declared);
        var catalogs = Visible(sources, DeclaredCatalog);

        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var repository = Calls(repositories[source.Path], @"(?:Find|Get|Query|Count|Any|Search|Refs|List|Read)\w*");
            var catalog = Calls(catalogs[source.Path], "ListAsync|SearchAsync|RefsAsync|GetAsync");

            foreach (var raw in source.Text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*')) continue;
                if (!Direct.IsMatch(line) && !Query.IsMatch(line) && !States.IsMatch(line)
                    && repository?.IsMatch(line) != true && catalog?.IsMatch(line) != true) continue;
                var key = $"{source.Path}|{line}";
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }
        return found;
    }

    // ── Проверки ──────────────────────────────────────────────────────────────

    [Fact]
    public void Каждое_место_чтения_названо_и_рассуждено()
    {
        var found = FindReads(Sources());

        var undeclared = found.Keys.Where(k => !Reads.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появилось место чтения записей, о котором архив не знает:\n" + string.Join("\n", undeclared) +
            "\n\nВпишите его в Reads и решите, что оно делает с архивной записью: Choice — список на " +
            "выбор, архивных в нём нет (нужен живой тест); Shown / Seen — показ уже стоящего; " +
            "Documents — только документы; Service — обслуживание. Молча оставлять нельзя: место, " +
            "отдавшее архивную запись на выбор, выглядит исправным.");

        var stale = Reads.Keys.Where(k => !found.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи строки, которых в коде больше нет:\n" + string.Join("\n", stale) +
            "\nУберите их — иначе перепись описывает несуществующее.");

        // Одинаковая строка стала встречаться в файле другое число раз: рядом с уже описанным чтением
        // появилось ещё одно такое же (или одно ушло) — и решение о нём никто не принимал.
        var recount = Reads.Where(r => found.TryGetValue(r.Key, out var times) && times != r.Value.Times)
            .Select(r => $"{r.Key}\n    в переписи — {r.Value.Times}, в коде — {found[r.Key]}")
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(recount.Count == 0,
            "Число одинаковых мест чтения в файле изменилось:\n" + string.Join("\n", recount) +
            "\nРешите, что новое место делает с архивной записью, и поправьте число (.X(n)).");
    }

    [Fact]
    public void У_каждого_решения_есть_причина_а_у_выбора_живой_тест()
    {
        // Живой тест стоит там, где для него готова обстановка: профиль уровня проверяется на чистой
        // базе своего класса — на общей базе хоста счетов профиль-тип достался бы всем его стройкам.
        var probes = new[] { typeof(ArchiveReadPurposeTests), typeof(LevelProfileTests), typeof(InvoiceArchiveTests) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var wrong = new List<string>();

        foreach (var (key, row) in Reads)
        {
            if (string.IsNullOrWhiteSpace(row.Why)) wrong.Add($"{key}\n    причина не названа");
            if (row.Kind is Kind.Choice or Kind.ByPurpose && row.Probes.Length == 0)
                wrong.Add($"{key}\n    выбор без живого теста: решение принято, но ничем не проверено");
            foreach (var probe in row.Probes.Where(p => !probes.Contains(p)))
                wrong.Add($"{key}\n    живого теста «{probe}» нет ни в ArchiveReadPurposeTests, ни в LevelProfileTests, " +
                    "ни в InvoiceArchiveTests");
        }

        Assert.True(wrong.Count == 0, "Перепись мест чтения неполна:\n" + string.Join("\n", wrong));
    }

    /// <summary>
    /// Отложить решение можно только на шаг, который ещё впереди. Сделав шаг, его номер убирают из
    /// <see cref="OpenSteps" /> — и всё, что на него было отложено и не сделано, становится отказом.
    /// К закрытию задачи список шагов пуст, а с ним и отложенные строки.
    /// </summary>
    [Fact]
    public void Отложенное_решение_ждёт_шага_который_ещё_впереди()
    {
        var overdue = Reads.Where(r => r.Value.Kind == Kind.Pending && !OpenSteps.Contains(r.Value.Step))
            .Select(r => $"{r.Key}\n    отложено на шаг {r.Value.Step}: {r.Value.Why}")
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(overdue.Count == 0,
            "Решение отложено на шаг, который уже сделан или не существует:\n" + string.Join("\n", overdue));
    }
}
