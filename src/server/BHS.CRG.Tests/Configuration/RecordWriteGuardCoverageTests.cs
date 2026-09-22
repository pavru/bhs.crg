using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перечень ВСЕХ мест, где данные объекта по схеме типа попадают в базу, — и вердикт у каждого:
/// охраняется или намеренно нет (issue #957).
///
/// Тест написан против нашей повторяющейся ошибки — «закрыл один вход из нескольких». Охрана,
/// подключённая к трём адресам из четырёх, выглядит работающей ровно так же, как подключённая ко
/// всем; разницу видно только в тот день, когда кто-то сохранит через четвёртый. Заведя новый
/// адрес записи, автор упрётся в красный тест и примет решение — пока оно ещё дёшево.
///
/// Проверяется В ОБЕ СТОРОНЫ: у объявленного охраняемым рядом обязан стоять вызов охраны, у
/// объявленного свободным — обязан НЕ стоять. Односторонняя проверка тихо соглашалась бы с тем, что
/// охрану из адреса убрали.
/// </summary>
public class RecordWriteGuardCoverageTests
{
    private static readonly string[] Projects = ["BHS.CRG.Application", "BHS.CRG.Api"];

    /// <summary>Как данные попадают в объект: присвоение или конструктор с готовыми данными.</summary>
    private static readonly Regex DataWrite = new(
        @"\.SetData\(|\.Update\(cmd\.DisplayName|\.Update\(cmd\.DocumentTypeId|DomainObject\.Create\(|QualityDocument\.Create\(",
        RegexOptions.Compiled);

    /// <summary>Сколько строк выше места записи ищется вызов охраны.</summary>
    private const int GuardLookback = 15;

    private const bool Guarded = true;
    private const bool Free = false;

    /// <summary>
    /// Ключ — «файл|строка кода», значение — вердикт и причина. Причина обязательна и у охраняемых
    /// тоже: перечень читают, чтобы понять замысел, а не чтобы убедиться, что он непустой.
    /// </summary>
    private static readonly Dictionary<string, (bool Guarded, string Why)> Writes = new()
    {
        ["BHS.CRG.Application/Documents/Handlers.cs|obj.SetData(cmd.Requisites);"] =
            (Guarded, "реквизиты документа — главный ручной путь"),
        ["BHS.CRG.Application/Documents/Handlers.cs|var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);"] =
            (Guarded, "создание записи общих данных"),
        ["BHS.CRG.Application/Documents/Handlers.cs|entry.Update(cmd.DisplayName, data, cmd.Aliases);"] =
            (Guarded, "правка записи общих данных — проверяется ПОСЛЕ слияния с привязками наборов"),
        ["BHS.CRG.Application/QualityDocs/Handlers.cs|var doc = QualityDocument.Create(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites, cmd.Scope, cmd.ScopeId, cmd.Source);"] =
            (Guarded, "создание документа качества — сегодняшнего прообраза записи модуля"),
        ["BHS.CRG.Application/QualityDocs/Handlers.cs|doc.Update(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites);"] =
            (Guarded, "правка документа качества"),
        ["BHS.CRG.Api/Endpoints/Documents/PrintFormEndpoints.cs|instance.SetData(patched);"] =
            (Guarded, "печатная форма кладёт прочитанные значения как есть — и пишет прямо в слое API, мимо MediatR"),

        ["BHS.CRG.Application/Documents/Handlers.cs|inst.SetData(System.Text.Json.JsonDocument.Parse(root.ToJsonString()));"] =
            (Free, "перенос ключа поля и починка аудита: их работа и есть трогать кривые данные; " +
                   "охрана сделала бы битую запись непочинимой, а правку схемы — обрывающейся на середине"),
        ["BHS.CRG.Application/Documents/Handlers.cs|source.SetData(data);"] =
            (Free, "перенос документа в другой комплект переписывает СВОИ же значения (вычищает " +
                   "неразрешимые ссылки); отказ сделал бы документ непереносимым"),
        ["BHS.CRG.Application/Documents/Handlers.cs|var obj = DomainObject.Create(cmd.DocumentTypeId, null, JsonDocument.Parse(\"{}\"),"] =
            (Free, "создание пустого документа в комплекте: вносить нечего"),
        ["BHS.CRG.Application/QualityDocs/SearchCommands.cs|var doc = QualityDocument.Create(cmd.DocumentTypeId, name, System.Text.Json.JsonDocument.Parse(\"{}\"),"] =
            (Free, "импорт из интернета заводит документ с пустыми реквизитами — вносить нечего"),
        ["BHS.CRG.Application/Generation/GenerateDocumentHandler.cs|instance.SetData(SchemaTags.PatchMetadata(instance.Data, taggedFields, meta));"] =
            (Free, "штамп метаданных на последнем шаге выпуска: отказ из-за чужого старого значения " +
                   "остановил бы генерацию; запертых полей штамп не пишет"),
        ["BHS.CRG.Application/Catalog/Handlers.cs|entity.Update(cmd.DisplayName, cmd.Data);"] =
            (Free, "устаревший каталог сущностей: типизирован строкой entityType, схемы типа у него " +
                   "нет вовсе — охране не с чем сверять"),
    };

    [Fact]
    public void Каждый_путь_записи_данных_назван_и_рассужден()
    {
        var found = FindWrites();

        var undeclared = found.Keys.Where(k => !Writes.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появился путь записи данных, о котором охрана не знает:\n" + string.Join("\n", undeclared) +
            "\n\nВпишите его в Writes: Guarded — если рядом обязан стоять вызов " +
            "WriteGuard.EnsureAllowedAsync, Free — с причиной, почему охраны там быть не должно.");

        var stale = Writes.Keys.Where(k => !found.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В перечне записи, которых в коде больше нет: " + string.Join("\n", stale) +
            "\nУберите строки — иначе перечень описывает несуществующее.");
    }

    [Fact]
    public void Охраняемый_путь_действительно_зовёт_охрану_а_свободный_нет()
    {
        var found = FindWrites();
        var wrong = new List<string>();

        foreach (var (key, (guarded, why)) in Writes)
        {
            if (!found.TryGetValue(key, out var guardNearby)) continue; // о пропаже говорит соседний тест
            if (guarded && !guardNearby)
                wrong.Add($"{key}\n    объявлен охраняемым ({why}), но вызова WriteGuard.EnsureAllowedAsync рядом нет");
            if (!guarded && guardNearby)
                wrong.Add($"{key}\n    объявлен свободным ({why}), а охрана рядом стоит — решение изменилось?");
        }

        Assert.True(wrong.Count == 0, "Перечень охраны разошёлся с кодом:\n" + string.Join("\n", wrong));
    }

    /// <summary>Место записи → стоит ли рядом (выше) вызов охраны.</summary>
    private static Dictionary<string, bool> FindWrites()
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var project in Projects)
            foreach (var file in SourceFiles(project))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!DataWrite.IsMatch(lines[i])) continue;
                    var key = $"{Relative(file)}|{lines[i].Trim()}";
                    var guarded = false;
                    for (var back = Math.Max(0, i - GuardLookback); back < i; back++)
                        if (lines[back].Contains("WriteGuard.EnsureAllowedAsync")) guarded = true;
                    // Одинаковые строки в одном файле сливаются: если хоть одна из них охраняется,
                    // считаем охраняемой пару — иначе перечень потребовал бы различать их номерами
                    // строк, а номера сдвигает любая правка выше по файлу.
                    found[key] = found.TryGetValue(key, out var was) && was || guarded;
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
