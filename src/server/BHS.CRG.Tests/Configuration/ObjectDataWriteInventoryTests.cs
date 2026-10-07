using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись мест, где данные УЖЕ ЛЕЖАЩЕГО объекта переписываются, — и как каждое уживается с чужой
/// правкой той же строки (issue #1232).
///
/// <para>Версию сверял только путь формы (#1214). Остальные писатели работали по схеме «прочитал —
/// изменил — сохранил»: правка формы, пришедшая между чтением и записью, пропадала под их снимком,
/// и оба получали успех. Исправить четыре найденных места мало — пятое появится так же незаметно,
/// поэтому новое присвоение данных краснеет здесь, пока о нём не принято решение.</para>
///
/// <para>Вердиктов три: <see cref="Locked" /> — объект прочитан под блокировкой строки
/// (<c>ReadForUpdate…</c> выше по коду); <see cref="Versioned" /> — запись сверяет названную
/// версию (<c>SaveSeenAsync</c> ниже); остальное — исключение с причиной. По исходникам и грубо, как
/// соседние переписи: что правка действительно не стирается, проверяет
/// <c>BackgroundWriterRaceTests</c>.</para>
///
/// <para>⚠️ Перепись видит присвоение ДАННЫХ. Запись строки целиком через
/// <c>Repository.Update</c> писателем, который данных не менял (имя, порядок, статус выпуска), сюда
/// не попадает — это отдельный класс, и он не закрыт.</para>
/// </summary>
public class ObjectDataWriteInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Application", "BHS.CRG.Api", "BHS.CRG.Infrastructure");

    private static readonly Regex DataWrite = new(
        @"\.SetData\(|entry\.Update\(cmd\.DisplayName|DomainObject\.Restore(Document)?\(|UPDATE domain_objects SET ""Data""",
        RegexOptions.Compiled);

    /// <summary>Сколько строк выше ищется чтение под блокировкой и сколько ниже — запись по версии.</summary>
    private const int Reach = 60;

    private const string Locked = "под блокировкой";
    private const string Versioned = "по версии";

    /// <summary>Ключ — «файл|строка кода»; значение — вердикт либо причина, почему место свободно.</summary>
    private static readonly Dictionary<string, string> Writes = new(StringComparer.Ordinal)
    {
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|entry.Update(cmd.DisplayName, data, cmd.Aliases);"] = Versioned,

        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|inst.SetData(System.Text.Json.JsonDocument.Parse(root.ToJsonString()));"] = Locked,
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|source.SetData(data);"] = Locked,
        ["BHS.CRG.Application/Generation/GenerateDocumentHandler.cs|instance.SetData(stamp(instance.Data));"] = Locked,
        ["BHS.CRG.Api/Endpoints/Documents/PrintFormEndpoints.cs|instance.SetData(patched);"] = Locked,

        ["BHS.CRG.Infrastructure/Maintenance/ImageBlobMigration.cs|UPDATE domain_objects SET \"Data\" = {json}::jsonb, \"UpdatedAt\" = {DateTimeOffset.UtcNow}"] =
            "перенос и уменьшение картинок: запись условная, одним UPDATE по версии строки — изменили " +
            "тем временем, и запись проходится заново; блокировка на время выгрузки держала бы форму",
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|obj.SetData(cmd.Requisites);"] =
            "форма реквизитов документа: версии у неё нет, из двух сохранений побеждает последнее — " +
            "решение #1214 касалось записей общих данных; фоновые писатели её правку не стирают",
        ["BHS.CRG.Infrastructure/DataFixups/ImageSizeToInstanceFixup.cs|obj.SetData(JsonDocument.Parse(migrated));"] =
            "разовая починка при старте, до приёма запросов: править объект в это время некому",
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.CommonData.cs|var entity = DomainObject.Restore("] =
            "восстановление копии: положить состояние из копии поверх текущего и есть его назначение; " +
            "форма, открытая до него, получит отказ по версии",
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.cs|var obj = DomainObject.RestoreDocument("] =
            "восстановление копии — то же для документов комплектов",
    };

    [Fact]
    public void Каждое_место_записи_данных_объекта_названо_и_рассуждено()
    {
        var found = Find();

        var undeclared = found.Keys.Where(k => !Writes.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появилось место, которое переписывает данные лежащего объекта, а как оно уживается с " +
            "чужой правкой той же строки — не сказано:\n" + string.Join("\n", undeclared) +
            "\n\nПрочитайте объект через IDomainObjectRepository.ReadForUpdateAsync (блокировка строки и " +
            "свежие данные) и впишите место в Writes как Locked — либо впишите причину, почему " +
            "стереть чужую правку оно не может или вправе.");

        var stale = Writes.Keys.Where(k => !found.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи места, которых в коде больше нет:\n" + string.Join("\n", stale) +
            "\nУберите строки — иначе перепись описывает несуществующее.");
    }

    [Fact]
    public void Вердикт_совпадает_с_кодом()
    {
        var found = Find();
        var wrong = new List<string>();
        foreach (var (key, verdict) in Writes)
        {
            if (!found.TryGetValue(key, out var site)) continue; // о пропаже говорит соседний тест
            if (verdict == Locked && !site.Locked)
                wrong.Add($"{key}\n    назван читающим под блокировкой, но ReadForUpdateAsync выше нет");
            if (verdict == Versioned && !site.Versioned)
                wrong.Add($"{key}\n    назван сверяющим версию, но SaveSeenAsync ниже нет");
            if (verdict != Locked && site.Locked)
                wrong.Add($"{key}\n    вердикт «{verdict}», а чтение под блокировкой рядом стоит — решение изменилось?");
        }
        Assert.True(wrong.Count == 0, "Перепись разошлась с кодом:\n" + string.Join("\n", wrong));
    }

    /// <summary>
    /// Место записи → что стоит рядом. Одинаковые строки одного файла сливаются, и требование к ним
    /// общее: «под блокировкой» — значит КАЖДАЯ; иначе вторая спряталась бы за первой.
    /// </summary>
    private static Dictionary<string, (bool Locked, bool Versioned)> Find()
    {
        var found = new Dictionary<string, (bool Locked, bool Versioned)>(StringComparer.Ordinal);
        foreach (var file in Projects.SelectMany(SourceFiles))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !DataWrite.IsMatch(lines[i])) continue;
                var locked = Enumerable.Range(Math.Max(0, i - Reach), i - Math.Max(0, i - Reach))
                    .Any(n => !IsComment(lines[n]) && lines[n].Contains("ReadForUpdate"));
                var versioned = Enumerable.Range(i + 1, Math.Min(lines.Length - i - 1, Reach))
                    .Any(n => !IsComment(lines[n]) && lines[n].Contains("SaveSeenAsync("));

                var key = $"{Relative(file)}|{lines[i].Trim()}";
                found[key] = found.TryGetValue(key, out var was)
                    ? (was.Locked && locked, was.Versioned && versioned)
                    : (locked, versioned);
            }
        }
        return found;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//");

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
