using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись путей записи счёта (задача C5, issue #1082): счёт пишут только через связку
/// <c>InvoiceDesk.WriteAsync</c>, а учётную дату считают только два места.
///
/// <para><b>От чего это.</b> Связка даёт каждой правке три вещи, которые адрес по памяти не сделает:
/// замок «запись против закрытия периода», отказ счёту, запертому закрытым периодом, и учётные даты
/// долей оплаченного счёта. Адрес, сохранивший счёт мимо неё, работает — и молча пишет в закрытый месяц
/// или оставляет долю оплаченного счёта без даты, то есть вне всех отчётов.</para>
///
/// <para>Проверка по исходникам, как у <see cref="PeriodClosureInventoryTests" />: на живом хосте
/// «забытый путь» не падает ничем, пока период открыт.</para>
/// </summary>
public class InvoiceWritePathTests
{
    private const string Module = "BHS.CRG.Modules.Costs";

    /// <summary>
    /// Сохранения мимо связки — поимённо и с причиной. Число — сколько таких сохранений в файле.
    /// </summary>
    private static readonly Dictionary<string, (int Saves, string Why)> OutsideTheDesk = new()
    {
        ["Endpoints/InvoiceEndpoints.cs"] =
            (1, "создание счёта: новый счёт не оплачен, а неоплаченный не принадлежит ни одному периоду"),
        ["Endpoints/WaybillEndpoints.cs"] =
            (6, "накладная — не счёт: денег в ней нет, учётных дат и замка против закрытия периода ей " +
                "не нужно; от одновременной правки её защищает версия строки (D1, issue #1083)"),
        ["Endpoints/InvoiceDesk.cs"] =
            (2, "сама связка: учётные даты после правки адреса — оплаченному и снятые у неоплаченного"),
        ["Endpoints/InvoiceRecognitionEndpoints.cs"] =
            (1, "черновик из скана — то же создание счёта: новый счёт не оплачен и периоду не принадлежит"),
        ["Endpoints/InvoiceScanRecognition.cs"] =
            (3, "запись о распознавании, а не счёт: постановка, отказ очереди и номер задачи пишут только " +
                "invoice_recognitions — счёт при них не меняется, и версия его остаётся прежней (issue #1077)"),
        ["Endpoints/InvoiceScanReading.cs"] =
            (2, "номер задачи и отказ обработчика: пишутся в invoice_recognitions, а в счёт не пишется ничего. " +
                "Само прочитанное ложится в счёт через связку (issue #1077)"),
        ["Endpoints/SupplierMatching.cs"] =
            (1, "соответствия наименований поставщика, а не счёт: пишется только supplier_matches, после " +
                "записи строк и вне её транзакции — отказ по ключу от соседнего счёта иначе губил бы само " +
                "сохранение строк (issue #1079)"),
        ["Endpoints/SupplierMatchListEndpoints.cs"] =
            (2, "правка и удаление соответствия из списка: пишется только supplier_matches, счёт не меняется. " +
                "Защита от устаревшей формы у соответствия своя — версия записи в If-Match (issue #1079)"),
    };

    /// <summary>Кто вправе считать учётную дату и спрашивать «закрыт ли день».</summary>
    private static readonly string[] MayAskThePeriod = ["Data/PaymentPosting.cs"];

    private static readonly Regex Save = new(@"\bSaveChanges(Async)?\(", RegexOptions.Compiled);
    // Входов у связки два: WriteAsync — правка из запроса, со сверкой версии формы; MergeAsync —
    // фоновая правка (issue #1077). Замок периода, отказ запертому счёту и учётные даты у них общие.
    private static readonly Regex Write = new(@"\bdesk\.(Write|Merge)Async\(", RegexOptions.Compiled);
    private static readonly Regex Bulk = new(@"\.Execute(Update|Delete|Sql\w*)(Async)?\(", RegexOptions.Compiled);
    private static readonly Regex Period = new(@"\.AccountingDate\(|\.IsClosed\(", RegexOptions.Compiled);

    [Fact]
    public void Счёт_сохраняют_только_через_связку_записи()
    {
        var strays = new List<string>();

        foreach (var (rel, code) in Sources())
        {
            // Контекст сам переводит гонку в отказ 409 — это обёртка сохранения, а не путь записи.
            if (rel == "Data/CostsDbContext.cs") continue;

            var saves = Save.Matches(code).Count;
            var writes = Write.Matches(code).Count;
            var allowed = OutsideTheDesk.TryGetValue(rel, out var listed) ? listed.Saves : 0;

            if (saves != writes + allowed)
                strays.Add($"{rel}: сохранений {saves}, связок записи {writes}, разрешено мимо {allowed}");
        }

        Assert.True(strays.Count == 0,
            "Счёт сохраняют мимо InvoiceDesk.WriteAsync (или перепись устарела):\n" + string.Join("\n", strays) + "\n\n" +
            "Каждая правка счёта, его строк и разноски идёт через связку: одна связка — одно сохранение. Только " +
            "она берёт замок против закрытия периода, отказывает запертому счёту и перекладывает учётные даты " +
            "оплаченного. Если сохранение мимо неё действительно нужно — впишите файл в OutsideTheDesk с причиной.");
    }

    /// <summary>
    /// Запись мимо отслеживания контекста связка не видит вовсе: ни счётчик сохранений выше, ни её
    /// собственное «тронуты ли деньги» (оно читает записи под сохранением). Такой записи в модуле нет —
    /// и появиться ей нельзя.
    /// </summary>
    [Fact]
    public void Модуль_не_пишет_мимо_отслеживания_контекста()
    {
        var bulk = Sources().Where(s => Bulk.IsMatch(s.Code)).Select(s => s.Rel).ToList();

        Assert.True(bulk.Count == 0,
            "Запись мимо отслеживания контекста (ExecuteUpdate, ExecuteDelete, ExecuteSql): " + string.Join(", ", bulk) +
            ".\n\nСвязка записи счёта узнаёт о тронутых деньгах по записям под сохранением; правку, прошедшую " +
            "мимо них, она не заметит — и оплаченный счёт останется с прежними учётными датами.");
    }

    [Fact]
    public void Учётную_дату_считает_одно_место()
    {
        var outsiders = Sources()
            .Where(s => !MayAskThePeriod.Contains(s.Rel) && Period.IsMatch(s.Code))
            .Select(s => s.Rel)
            .ToList();

        Assert.True(outsiders.Count == 0,
            "Учётную дату или «закрыт ли день» спросили мимо PaymentPosting и ClosedPeriodGuard: " +
            string.Join(", ", outsiders) + ".\n\nФорма обещает перенос, запись его кладёт, разноска перекладывает, " +
            "реестр показывает — и все обязаны считать одной функцией: посчитанное вторым местом однажды " +
            "разойдётся с первым, и узнают об этом по отчёту закрытого месяца.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var files = Sources().ToDictionary(s => s.Rel, s => s.Code);

        var stale = OutsideTheDesk.Keys.Where(rel => !files.TryGetValue(rel, out var code) || !Save.IsMatch(code))
            .Concat(MayAskThePeriod.Where(rel => !files.TryGetValue(rel, out var code) || !Period.IsMatch(code)))
            .ToList();

        Assert.True(stale.Count == 0, "В переписи файлы, где названного больше нет: " + string.Join(", ", stale));
    }

    /// <summary>Исходники модуля без комментариев: упоминание в объяснении — не обращение.</summary>
    private static IEnumerable<(string Rel, string Code)> Sources()
    {
        var root = Path.Combine(SolutionDir, Module);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(f => (
                Path.GetRelativePath(root, f).Replace('\\', '/'),
                string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")))));
    }

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает исходники и без них проверять нечего.");
    }
}
