using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись читателей хранилища: кто называет владельца файла и откуда его берёт (ТЗ CORE-37.2).
///
/// <para>Хранилище отдаёт без владельца только файлы ядра, поэтому служба ядра, прочитавшая файл
/// модуля без владельца, получает «файла нет» — и ломается видимо. Опасна обратная ошибка: владелец,
/// названный не тем, кто вправе. Модуль чужой код назвать не может (порт знает свой по построению), а
/// служба ядра — может: строкой, параметром из запроса, маркером «любой». Поэтому перепись следит за
/// каждым вызовом чтения вне хранилища: без владельца или с <c>BlobOwner.Core</c> — ядро, любое другое
/// имя владельца — только в файлах из списка ниже, с причиной, а <c>BlobOwner.Any</c> — только в
/// резервной копии.</para>
///
/// <para>⚠️ Проверка читает ИСХОДНИКИ — тем же приёмом, что <see cref="ActivityLogInventoryTests" />:
/// в сборке «каким выражением назван владелец» не отражено никак.</para>
/// </summary>
public class BlobReaderInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>Сама обёртка с реестром: владельца она проверяет, а не называет.</summary>
    private const string StorageDir = "BHS.CRG.Infrastructure/Storage/";

    /// <summary>
    /// Кто вправе читать от имени владельца, отличного от ядра, — и откуда он его берёт. Добавляя
    /// строку, вы принимаете решение: этот код читает файлы модулей, и владелец здесь не выдуман.
    /// </summary>
    private static readonly Dictionary<string, string> MayNameOwner = new()
    {
        ["BHS.CRG.Api/Modules/Ports/ModuleBlobsPort.cs"] =
            "порт модуля: код модуля задан при построении keyed-сервиса, параметра у модуля нет",
        ["BHS.CRG.Api/Renditions/RenditionStore.cs"] =
            "читаемый образ: владельца называет порт образов модуля, тоже keyed по коду модуля",
        ["BHS.CRG.Infrastructure/Maintenance/OrphanBlobCleanup.cs"] =
            "уборка осиротевших: владелец — из записи реестра, которую она убирает",
    };

    /// <summary>Резервная копия переносит файлы всех владельцев — ей одной разрешён «любой».</summary>
    private static readonly string[] MayReadAny =
    [
        "BHS.CRG.Infrastructure/Backup/",
        "BHS.CRG.Api/Modules/ModuleSchemaBackup",
    ];

    private static readonly Regex Reader = new(@"\.(DownloadAsync|GetSizeAsync|FindAbsentAsync)\(", RegexOptions.Compiled);

    [Fact]
    public void Владельца_называют_только_те_кому_положено()
    {
        var violations = new List<string>();
        foreach (var file in Projects.SelectMany(SourceTree.Files))
        {
            var rel = SourceTree.Relative(file);
            if (rel.StartsWith(StorageDir, StringComparison.Ordinal)) continue;
            violations.AddRange(Check(rel, File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0,
            "Владелец файла хранилища назван там, где его называть не положено:\n" + string.Join("\n", violations) +
            "\n\nСлужба ядра читает без владельца — то есть только файлы ядра. Файл модуля читает порт модуля; " +
            "если службе ядра он всё же нужен, владельца она берёт из записи (не строкой и не из запроса), и " +
            "файл вписывается в MayNameOwner с причиной. «Любой» (BlobOwner.Any) — только резервной копии.");
    }

    /// <summary>
    /// Сторож обязан срабатывать: на образцах нарушений он краснеет, на чтении ядра — молчит. Без
    /// этого поломка разбора (вызов через перенос строки, именованный аргумент) превратила бы перепись
    /// в зелёную при любом коде.
    /// </summary>
    [Fact]
    public void Сторож_срабатывает_на_образцах()
    {
        const string file = "BHS.CRG.Infrastructure/Generation/Sample.cs";

        Assert.Empty(Check(file, "await blob.DownloadAsync(path, ct);"));
        Assert.Empty(Check(file, "await blob.DownloadAsync(path, ct: ct);"));
        Assert.Empty(Check(file, "await blob.GetSizeAsync(path, CancellationToken.None);"));
        Assert.Empty(Check(file, "await blob.DownloadAsync(path, BlobOwner.Core, ct);"));
        Assert.Empty(Check(file, "/// <see cref=\"IBlobStorage.DownloadAsync(string, string, CancellationToken)\" />"));

        Assert.Single(Check(file, "await blob.DownloadAsync(scan.Path, \"costs\", ct);"));
        Assert.Single(Check(file, "await blob.DownloadAsync(\n    path,\n    request.Owner,\n    ct);"));
        Assert.Single(Check(file, "await blob.FindAbsentAsync(paths, BlobOwner.Any, ct);"));
        Assert.Empty(Check("BHS.CRG.Infrastructure/Backup/BackupService.cs", "await blob.DownloadAsync(p, BlobOwner.Any, ct);"));
        Assert.Single(Check("BHS.CRG.Api/Renditions/RenditionStore.cs", "await blobs.DownloadAsync(p, BlobOwner.Any, ct);"));
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayNameOwner.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SourceTree.SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || OwnersNamed(File.ReadAllText(path)).All(o => o is null or "BlobOwner.Core");
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, которые владельца больше не называют: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись разрешает то, чего уже не делают.");
    }

    private static IEnumerable<string> Check(string rel, string text)
    {
        var mayAny = MayReadAny.Any(prefix => rel.StartsWith(prefix, StringComparison.Ordinal));
        var mayName = MayNameOwner.ContainsKey(rel);
        foreach (var (owner, index) in Calls(text))
        {
            if (owner is null || owner == "BlobOwner.Core") continue;
            if (owner == "BlobOwner.Any" ? mayAny : mayName) continue;
            yield return $"{rel}:{SourceTree.LineOf(text, index)} — владелец «{owner}»";
        }
    }

    private static IEnumerable<string?> OwnersNamed(string text) => Calls(text).Select(c => c.Owner);

    /// <summary>
    /// Вызовы чтения и владелец каждого — второй аргумент, если он не токен отмены; <c>null</c> — без
    /// владельца. Строки комментариев пропускаются: ссылки <c>cref</c> на перегрузки вызовами не являются.
    /// </summary>
    private static IEnumerable<(string? Owner, int Index)> Calls(string text)
    {
        foreach (Match m in Reader.Matches(text))
        {
            if (SourceTree.LineAt(text, m.Index).TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            var args = Arguments(text, m.Index + m.Length);
            yield return (args.Count >= 2 && !IsToken(args[1]) ? args[1] : null, m.Index);
        }
    }

    private static readonly Regex Named = new(@"^(\w+)\s*:\s*(.+)$", RegexOptions.Singleline);

    private static bool IsToken(string arg)
    {
        if (Named.Match(arg) is { Success: true } named)
            return named.Groups[1].Value is "ct" or "cancellationToken";
        return arg is "ct" or "cancellationToken" or "default" or "CancellationToken.None";
    }

    /// <summary>Аргументы вызова верхнего уровня: скобки и строки считаются, запятые внутри них — нет.</summary>
    private static List<string> Arguments(string text, int from)
    {
        var args = new List<string>();
        var depth = 0;
        var start = from;
        var inString = false;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth > 0: depth--; break;
                case ')':
                    args.Add(text[start..i].Trim());
                    return args;
                case ',' when depth == 0:
                    args.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        return args;
    }
}
