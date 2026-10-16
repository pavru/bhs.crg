using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к книге проводок счёта и к версиям счёта (E2, issue #1100): книгу пишет одно
/// место — <c>InvoiceBook</c>, версии — вход исправления связки записи.
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
        [$"{Module}/Data/InvoiceBook.cs"] =
            "единственное место записи: блокировка строки счёта, сверка, номер записи; и чтение книги одного счёта — " +
            "для записи и для истории на форме (адрес …/book, PR 6b-2 E2); день и причина последней записи — " +
            "пометке реестра «Исправлен после закрытия» (PR 6b-3 E2), без денег книги",
        [$"{Module}/Data/CostsDbContext.cs"] = "объявление наборов",
        [$"{Module}/Data/InvoiceBookEntry.cs"] = "сами сущности и их отображение на таблицы",
        [$"{Module}/CostsReferences.cs"] = "объявление колонок с идентификаторами ядра: запросов нет",
        [$"{Module}/Data/InvoiceVersion.cs"] = "сама версия и её отображение на таблицу",
        [$"{Module}/Data/CostViews.cs"] =
            "читатель книги по многим счетам: взгляд «на момент закрытия» для отчёта и диалога закрытия (PR 4 E2) — " +
            "только чтение, без отслеживания",
        [$"{Module}/Endpoints/InvoiceDesk.Correction.cs"] =
            "единственное место записи версий: исправление пишет версию под блокировкой строки счёта, " +
            "в одной транзакции с книгой; книгу — через InvoiceBook",
    };

    // Набор достаётся и мимо имени свойства — Set<InvoiceBookLine>() из любого места контекста (ревью
    // PR #1357); поэтому и обращение по типу.
    private static readonly Regex Book = new(
        @"\bInvoiceBook(Entries|Lines)\b|\binvoice_book_(entries|lines)\b|\bInvoiceBook(Entry|Line)\.Create\(" +
        @"|\.RecordBook\(|\bSet<InvoiceBook(Entry|Line)>" +
        @"|\bInvoiceVersions\b|\binvoice_versions\b|\bInvoiceVersion\.Create\(|\bSet<InvoiceVersion>",
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

    /// <summary>
    /// Автора записи книги модуль собирает только фабрикой <c>BookAuthor.Of(user, …)</c> — с именем на
    /// момент записи (PR 6b-2 E2). Позиционный конструктор собирается и без имени, и место записи,
    /// написанное по образцу тестов, оставило бы в истории счёта «—» у записей, автор которых известен.
    /// </summary>
    [Fact]
    public void Автор_книги_собирается_только_фабрикой_с_именем()
    {
        var bypass = new List<string>();
        foreach (var file in SourceTree.Files(Module))
        {
            var rel = SourceTree.Relative(file);
            if (rel == $"{Module}/Data/InvoiceBook.cs") continue;

            var text = string.Join("\n", File.ReadAllLines(file).Select(l => l.TrimStart().StartsWith("//") ? "" : l));
            foreach (Match m in Regex.Matches(text, @"\bnew\s+BookAuthor\s*\("))
                bypass.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}");
        }

        Assert.True(bypass.Count == 0,
            "Автор записи книги собран конструктором, а не BookAuthor.Of(user, …):\n" + string.Join("\n", bypass) +
            "\n\nБез имени история счёта покажет прочерк у записи, автор которой известен.");
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
