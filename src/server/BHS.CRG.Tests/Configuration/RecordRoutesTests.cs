using System.Text.RegularExpressions;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Экраны записей в клиенте названы КОДАМИ ТИПОВ, которые объявляет сервер (задача G4, issue #1097):
/// строка таблицы ведёт в форму записи по паре «тип записи таблицы + ключ строки», а экран по коду
/// типа находит список <c>RECORD_ROUTES</c> в <c>src/client/src/shared/ui/recordRoutes.ts</c>.
///
/// <para>Код там — строка-копия. Переименуй тип на сервере — и серверные тесты, и тесты клиента
/// останутся зелёными, а ссылки из реестра молча исчезнут у всех: «нет ссылки» выглядит так же, как
/// «нет права». Заметил бы это только живой прогон, а он слияния не запрещает. Поэтому сверка — здесь,
/// в обязательной проверке.</para>
/// </summary>
public class RecordRoutesTests
{
    private const string ClientFile = "src/client/src/shared/ui/recordRoutes.ts";

    [Fact]
    public void Каждый_тип_записи_с_экраном_в_клиенте_стоит_за_таблицей_модуля()
    {
        var declared = new ModuleTableCatalog([new CostsModule()]).All
            .Select(e => e.Table.RecordType).Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
        var routed = RoutedTypes();

        // Пустой список — не «расхождений нет», а «список не прочитан»: файл переписали, и сверять
        // стало нечего. Зелёный тест тогда был бы отчётом о работе, которой не было.
        Assert.NotEmpty(routed);
        Assert.All(routed, type => Assert.True(declared.Contains(type),
            $"В {ClientFile} экран назван для типа записи «{type}», а таблицы модулей с таким типом нет: " +
            $"объявлены {string.Join(", ", declared)}. Ссылка из строки таблицы на этот экран не появится."));
    }

    /// <summary>Ключи списка <c>RECORD_ROUTES</c> — коды типов в кавычках до двоеточия.</summary>
    private static List<string> RoutedTypes()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, ClientFile));
        var block = Regex.Match(text, @"RECORD_ROUTES[^=]*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(block.Success, $"В {ClientFile} не нашёлся список RECORD_ROUTES — сверять нечего.");
        return [.. Regex.Matches(block.Groups["body"].Value, @"'(?<type>[^']+)'\s*:").Select(m => m.Groups["type"].Value)];
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory +
                " — тест сверяет список экранов клиента с объявлениями модулей.");
    }
}
