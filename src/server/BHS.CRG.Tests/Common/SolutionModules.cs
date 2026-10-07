using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Common;

/// <summary>
/// Проекты модулей решения — один ответ для всех сторожей (задача M3, issue #1071).
///
/// <para><b>Зачем общий.</b> Переписи этапа 1 сканировали прибитый список
/// <c>Api</c>/<c>Application</c>/<c>Infrastructure</c>. Проект модуля в него не входил, и каждая из
/// них оставалась зелёной, что бы в модуле ни написали: класс сторожей переставал действовать ровно
/// там, где появлялся новый код. Список, который перепись собирает сама, новый модуль получает без
/// правки переписи — забыть его вписать нельзя.</para>
///
/// <para>Отбор — по ССЫЛКЕ на контракты ядра, а не по имени каталога: имя — соглашение, его проверяет
/// <c>ModuleBoundaryTests</c>, и проект, названный иначе, выпал бы из отбора молча. Что отбор не
/// опустел и не разошёлся с поставкой, сверяет <c>InventoryScopeTests</c>.</para>
/// </summary>
public static class SolutionModules
{
    public const string ContractsProject = "BHS.CRG.Modules";

    /// <summary>
    /// Проекты, которые ссылаются на контракты ядра, но модулями не являются, и почему. Добавляя
    /// сюда строку, вы принимаете решение — именно этого сторож и добивается.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownHosts = new Dictionary<string, string>
    {
        ["BHS.CRG.Api"] = "хост: перечисляет модули в корне композиции и держит обёртку `id`, пока её код не переехал",
        ["BHS.CRG.Tests"] = "тесты: проверяют сам механизм модулей",
    };

    // ⚠️ Оба выражения — ВЫШЕ списка проектов: статические поля заполняются сверху вниз, и список,
    // собранный раньше них, разбирал бы файлы проектов выражением, которого ещё нет.
    private static readonly Regex ProjectReferenceTag = new(
        @"<ProjectReference\b[^>]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IncludeAttribute = new(
        @"\bInclude\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Модули решения с их ссылками на проекты — в порядке имён.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> References)> Projects { get; } = [.. Find()];

    /// <summary>Имена проектов модулей.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Projects.Select(p => p.Name)];

    /// <summary>
    /// Проекты ядра, названные переписью, проект контрактов И все проекты модулей. Перепись зовёт это
    /// вместо своего списка: назвать ядро и забыть модули так нельзя.
    ///
    /// <para>Контракты — тоже здесь, хотя модулем они не являются. Кода там не меньше, чем объявлений
    /// (<c>ModuleDbContext</c>, замок записи против закрытия периода, реестр модулей), у него та же
    /// база под рукой — и ни ядром, ни модулем его никто не называл: проект выпадал из всех переписей
    /// разом (ревью PR #1250).</para>
    /// </summary>
    public static string[] WithCore(params string[] core) =>
        [.. core.Append(ContractsProject).Distinct(StringComparer.Ordinal), .. Names];

    /// <summary>
    /// Ссылки на проекты из файла проекта.
    ///
    /// Разбор в два шага — тег целиком, потом атрибут — потому что однострочный регекс требовал
    /// <c>Include</c> ПЕРВЫМ атрибутом и только в двойных кавычках, а
    /// <c>&lt;ProjectReference Condition="…" Include='…'&gt;</c> проходил бы мимо правила
    /// (поймано на ревью #968). Сторож, который обходится перестановкой атрибутов, — не сторож.
    /// </summary>
    public static IEnumerable<string> ReferencedProjects(string csproj)
    {
        foreach (Match tag in ProjectReferenceTag.Matches(csproj))
        {
            var include = IncludeAttribute.Match(tag.Value);
            if (include.Success)
                yield return (include.Groups[1].Success ? include.Groups[1] : include.Groups[2]).Value;
        }
    }

    private static IEnumerable<(string Name, IReadOnlyList<string> References)> Find()
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var csproj in Directory
                     .EnumerateFiles(SourceTree.SolutionDir, "*.csproj", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            if (csproj.Contains($"{sep}obj{sep}") || csproj.Contains($"{sep}bin{sep}")) continue;

            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name == ContractsProject || KnownHosts.ContainsKey(name)) continue;

            var references = ReferencedProjects(File.ReadAllText(csproj))
                .Select(r => Path.GetFileNameWithoutExtension(r.Replace('\\', '/')))
                .ToList();

            if (references.Contains(ContractsProject))
                yield return (name, references);
        }
    }
}
