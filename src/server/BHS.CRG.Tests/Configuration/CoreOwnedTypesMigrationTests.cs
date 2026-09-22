using System.Text.RegularExpressions;
using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Список типов ядра записан ДВАЖДЫ: в реестре <see cref="CoreOwnedTypes" /> (им пользуются код и
/// восстановление из старой копии) и дословно в миграции, которая расставила владельцев
/// (issue #955, ТЗ CORE-30). Тест держит обе записи одинаковыми.
///
/// Почему записей две, а не одна. Миграция — история: она однажды выполнилась и описывает решение,
/// принятое в тот день. Читай она реестр, первый же перенос справочников в ядро (задача G1)
/// задним числом сменил бы смысл уже выполненного шага, и на новой установке владельцы
/// разошлись бы со старой молча. А расходиться им нельзя, пока реестр ещё описывает ТУ ЖЕ
/// передачу: разойдясь, они сделают разное на пустой базе и на базе с историей.
///
/// ⚠️ Чего этот тест НЕ доказывает: что список совпадает с рабочей базой заказчика. Типов
/// заказчика здесь нет и быть не может — их приносит его база. Это доказывается только прогоном
/// миграции на копии рабочей базы, и другого способа нет.
///
/// Когда список ядра ПЕРЕСТАНЕТ соответствовать этой миграции — например, когда справочники
/// поедут в ядро следующей задачей, — тест надо не чинить, а переписать: сверять реестр с ТОЙ
/// миграцией, которая станет последней передачей владельцев.
/// </summary>
public class CoreOwnedTypesMigrationTests
{
    private const string MigrationFile =
        "src/server/BHS.CRG.Infrastructure/Migrations/20260922043724_TypeModuleOwner.cs";

    [Fact]
    public void Реестр_и_миграция_называют_один_список()
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot, MigrationFile));

        // Положительный якорь: если разбор файла сломается, сравнение двух пустых списков
        // прошло бы молча, ничего не проверив.
        const string head = """update document_types set "Module" = 'core' where "Code" in (""";
        Assert.Contains(head, sql);

        // Читается ровно перечень кодов ядра, а не все строки в кавычках: рядом в файле лежит
        // второй запрос, и он тоже пишет владельца.
        var start = sql.IndexOf(head, StringComparison.Ordinal) + head.Length;
        var list = sql[start..sql.IndexOf(");", start, StringComparison.Ordinal)];

        var inMigration = Regex.Matches(list, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(inMigration);
        Assert.Equal(CoreOwnedTypes.Codes.OrderBy(c => c, StringComparer.Ordinal),
                     inMigration.OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>
    /// Код типа постоянен (ТЗ TYPE-6): им адресуются печатные блоки. Пробел или заглавная буква в
    /// списке означает, что миграция не найдёт тип и тихо оставит его модулю — то есть отберёт у
    /// ядра справочник, ничего при этом не сказав.
    /// </summary>
    [Fact]
    public void Коды_ядра_без_пробелов_и_дубликатов()
    {
        Assert.All(CoreOwnedTypes.Codes, code =>
        {
            Assert.Equal(code.Trim(), code);
            Assert.DoesNotContain(' ', code);
        });
        Assert.Equal(CoreOwnedTypes.Codes.Count, CoreOwnedTypes.Codes.Distinct(StringComparer.Ordinal).Count());
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory);
    }
}
