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
/// (номера строк сдвигает любая правка выше), поэтому решение у них обязано быть одно.</para>
///
/// <para><b>Перепись доказывает, что решение ПРИНЯТО, а не что оно исполнено.</b> Второй слой —
/// <see cref="ArchiveReadPurposeTests" />: у каждой строки «выбор» назван живой тест, который кладёт
/// запись в архив и смотрит, что в выборе её нет. Строка «выбор» без такого теста — отказ.</para>
///
/// <para>Чего перепись НЕ видит: чтения через обобщённый код, где тип объекта не назван в файле, и
/// клиент, попросивший у сервера «показ» для своего выбора. Первое — редкость; второе ловит только
/// правило записи (новая ссылка на архивную запись — отказ), оно приезжает последним шагом задачи.</para>
/// </summary>
public partial class ArchiveReadInventoryTests
{
    private static readonly string[] Projects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure", "BHS.CRG.Modules.Costs"];

    /// <summary>Шаги задачи #1185, которые ещё не сделаны. Отложить решение можно только на них.</summary>
    private static readonly int[] OpenSteps = [2, 3, 4];

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

    private sealed record Row(Kind Kind, string Why, string[] Probes, int Step = 0);

    private static Row Choice(string why, params string[] probes) => new(Kind.Choice, why, probes);
    private static Row ByPurpose(string why, params string[] probes) => new(Kind.ByPurpose, why, probes);
    private static Row Shown(string why) => new(Kind.Shown, why, []);
    private static Row Seen(string why) => new(Kind.Seen, why, []);
    private static Row Documents(string why) => new(Kind.Documents, why, []);
    private static Row Service(string why) => new(Kind.Service, why, []);
    private static Row Pending(int step, string why) => new(Kind.Pending, why, [], step);

    // ── Что считается чтением ─────────────────────────────────────────────────

    /// <summary>Имя, под которым файл держит репозиторий объектов.</summary>
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

    /// <summary>Порты модулей: справочник и состояние ссылки на запись.</summary>
    private static readonly Regex Port = new(
        @"\b[cC]atalog\.(?:ListAsync|SearchAsync|RefsAsync|GetAsync)\(|\.StatesAsync\(ReferenceTarget\.Record",
        RegexOptions.Compiled);

    private static HashSet<string> FindReads()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in Projects)
            foreach (var file in SourceTree.Files(project))
            {
                var text = File.ReadAllText(file);
                var names = Declared.Matches(text).Select(m => Regex.Escape(m.Groups[1].Value)).Distinct().ToList();
                var repository = names.Count == 0
                    ? null
                    : new Regex(@"\b(?:" + string.Join('|', names) + @")\.(?:Find|Get|Query|Count|Any|Search|Refs|List)\w*\(");

                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*')) continue;
                    if (Direct.IsMatch(line) || Query.IsMatch(line) || Port.IsMatch(line)
                        || repository?.IsMatch(line) == true)
                        found.Add($"{SourceTree.Relative(file)}|{line}");
                }
            }
        return found;
    }

    // ── Проверки ──────────────────────────────────────────────────────────────

    [Fact]
    public void Каждое_место_чтения_названо_и_рассуждено()
    {
        var found = FindReads();

        var undeclared = found.Where(k => !Reads.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появилось место чтения записей, о котором архив не знает:\n" + string.Join("\n", undeclared) +
            "\n\nВпишите его в Reads и решите, что оно делает с архивной записью: Choice — список на " +
            "выбор, архивных в нём нет (нужен живой тест); Shown / Seen — показ уже стоящего; " +
            "Documents — только документы; Service — обслуживание. Молча оставлять нельзя: место, " +
            "отдавшее архивную запись на выбор, выглядит исправным.");

        var stale = Reads.Keys.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи строки, которых в коде больше нет:\n" + string.Join("\n", stale) +
            "\nУберите их — иначе перепись описывает несуществующее.");
    }

    [Fact]
    public void У_каждого_решения_есть_причина_а_у_выбора_живой_тест()
    {
        var probes = typeof(ArchiveReadPurposeTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var wrong = new List<string>();

        foreach (var (key, row) in Reads)
        {
            if (string.IsNullOrWhiteSpace(row.Why)) wrong.Add($"{key}\n    причина не названа");
            if (row.Kind is Kind.Choice or Kind.ByPurpose && row.Probes.Length == 0)
                wrong.Add($"{key}\n    выбор без живого теста: решение принято, но ничем не проверено");
            foreach (var probe in row.Probes.Where(p => !probes.Contains(p)))
                wrong.Add($"{key}\n    живого теста «{probe}» в ArchiveReadPurposeTests нет");
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
