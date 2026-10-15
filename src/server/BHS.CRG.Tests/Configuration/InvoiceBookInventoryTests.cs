using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к книге проводок счёта (E2, issue #1100): книгу пишет одно место —
/// <c>InvoiceBook</c>.
///
/// <para><b>От чего это.</b> Запись в книгу обязана три вещи, которых адрес по памяти не сделает:
/// запереть строку счёта до чтения книги, сверить книгу с документом и подвинуть номер записи у
/// счёта — без последнего форма, открытая до записи, не узнает, что счёт изменили. Строка, добавленная
/// мимо, работает и молча ломает «на момент закрытия». Правку и удаление строк отвергает сам контекст
/// (<c>IAppendOnly</c>), а запись мимо отслеживания (<c>ExecuteUpdate</c>, голый SQL) — сторож
/// <c>InvoiceWritePathTests</c>; эта перепись закрывает третий путь — второе место записи.</para>
///
/// <para>Таблицы названы, а не только наборы: до книги дотягивается и сырой запрос. Чтение книги
/// (взгляд «на момент закрытия», диалог закрытия) придёт своими PR — и впишется сюда с причиной.</para>
/// </summary>
public class InvoiceBookInventoryTests
{
    private const string Module = "BHS.CRG.Modules.Costs";

    /// <summary>Кому позволено обращаться к книге — и почему.</summary>
    private static readonly Dictionary<string, string> MayTouchTheBook = new()
    {
        [$"{Module}/Data/InvoiceBook.cs"] = "единственное место записи: блокировка строки счёта, сверка, номер записи",
        [$"{Module}/Data/CostsDbContext.cs"] = "объявление наборов",
        [$"{Module}/Data/InvoiceBookEntry.cs"] = "сами сущности и их отображение на таблицы",
        [$"{Module}/CostsReferences.cs"] = "объявление колонок с идентификаторами ядра: запросов нет",
    };

    // Набор достаётся и мимо имени свойства — Set<InvoiceBookLine>() из любого места контекста (ревью
    // PR #1357); поэтому и обращение по типу.
    private static readonly Regex Book = new(
        @"\bInvoiceBook(Entries|Lines)\b|\binvoice_book_(entries|lines)\b|\bInvoiceBook(Entry|Line)\.Create\(" +
        @"|\.RecordBook\(|\bSet<InvoiceBook(Entry|Line)>",
        RegexOptions.Compiled);

    [Fact]
    public void К_книге_обращается_только_её_место_записи()
    {
        var outsiders = new List<string>();
        foreach (var file in SourceTree.Files(Module))
        {
            var rel = SourceTree.Relative(file);
            if (MayTouchTheBook.ContainsKey(rel)) continue;

            var text = string.Join("\n", File.ReadAllLines(file).Select(l => l.TrimStart().StartsWith("//") ? "" : l));
            foreach (Match m in Book.Matches(text))
                outsiders.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}");
        }

        Assert.True(outsiders.Count == 0,
            "К книге проводок счёта обратились мимо InvoiceBook:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Книгу пишет только InvoiceBook.AppendAsync: только он запирает строку счёта, сверяет книгу с " +
            "документом и двигает номер записи. Если обращение нужно (чтение для отчёта) — впишите файл в " +
            "MayTouchTheBook с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheBook.Keys
            .Where(rel => !File.Exists(Path.Combine(SourceTree.SolutionDir, rel))
                          || !Book.IsMatch(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, rel))))
            .ToList();

        Assert.True(stale.Count == 0, "В переписи книги файлы, где обращения больше нет: " + string.Join(", ", stale));
    }
}
