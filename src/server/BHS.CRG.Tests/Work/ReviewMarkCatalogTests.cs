using System.Text.RegularExpressions;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Каталог пометок очереди приёмки (ТЗ WORK-28; change <c>work-16-review</c>): у каждого кода текст и источник данных, и
/// коды сервера равны <c>WORK_MARK_CODES</c> клиента (<c>src/client/src/shared/api/work.ts</c>).
///
/// <para>Код клиента — строка-копия. Переименуй код на сервере — отбор клиента по старому коду молча вернёт 400, а
/// счётчик под ним — «нет числа». Поэтому сверка — здесь, в обязательной проверке (приём <c>RecordRoutesTests</c>). Пустой
/// список клиента — отказ: «список не прочитан», а не «расхождений нет».</para>
/// </summary>
public class ReviewMarkCatalogTests
{
    private const string ClientFile = "src/client/src/shared/api/work.ts";

    [Fact]
    public void У_каждого_кода_текст_и_источник()
    {
        Assert.All(ReviewMarks.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Text), $"У пометки «{m.Code}» нет текста.");
            Assert.False(string.IsNullOrWhiteSpace(m.Source), $"У пометки «{m.Code}» не назван источник данных.");
        });
        Assert.Equal(ReviewMarks.All.Count, ReviewMarks.All.Select(m => m.Code).Distinct().Count());
    }

    [Fact]
    public void Коды_этого_шага()
    {
        string[] codes =
        [
            ReviewMarks.EditedAfterSubmit, ReviewMarks.SamePerson, ReviewMarks.ManagerEdit, ReviewMarks.SiteNotAssigned,
            ReviewMarks.PhotosPending, ReviewMarks.CrewMismatch, ReviewMarks.LateDelivery, ReviewMarks.FieldsMissing,
        ];
        Assert.All(codes, code => Assert.NotNull(ReviewMarks.Of(code)));
        Assert.NotNull(ReviewMarks.Of(ReviewMarks.PhotosPending)!.Has);
        Assert.NotNull(ReviewMarks.Of(ReviewMarks.CrewMismatch)!.Has);
        Assert.NotNull(ReviewMarks.Of(ReviewMarks.LateDelivery)!.Has);
        Assert.Equal("не заполнены поля: 2", Assert.Single(ReviewMarks.Dtos([], 0, 2)).Text);
    }

    [Fact]
    public void Коды_сервера_равны_кодам_клиента() =>
        Assert.Empty(Mismatches(ReviewMarks.All.Select(m => m.Code).ToList(), ClientCodes()));

    /// <summary>
    /// Порядок — тоже договор: пометки строки приходят в порядке каталога сервера, а отбор клиента строится по
    /// <c>WORK_MARK_CODES</c>; переставленный с одной стороны код развёл бы их молча.
    /// </summary>
    [Fact]
    public void Сверка_ловит_другой_порядок() =>
        Assert.Contains(Mismatches(["a", "b"], ["b", "a"]), p => p.Contains("порядок"));

    [Fact]
    public void Сверка_ловит_лишний_код_с_любой_стороны_и_пустой_список()
    {
        Assert.Contains(Mismatches(["a", "b"], ["a"]), p => p.Contains("«b»") && p.Contains("клиент"));
        Assert.Contains(Mismatches(["a"], ["a", "c"]), p => p.Contains("«c»") && p.Contains("сервер"));
        Assert.NotEmpty(Mismatches(["a"], []));
        Assert.Empty(Mismatches(["a", "b"], ["a", "b"]));
    }

    /// <summary>Расхождения каталогов; пустой список клиента — тоже расхождение.</summary>
    private static List<string> Mismatches(IReadOnlyList<string> server, IReadOnlyList<string> client)
    {
        if (client.Count == 0) return [$"В {ClientFile} не прочитан ни один код WORK_MARK_CODES — сверять нечего."];
        List<string> problems =
        [
            .. server.Except(client).Select(c => $"Код «{c}» есть на сервере, а у клиента ({ClientFile}) — нет."),
            .. client.Except(server).Select(c => $"Код «{c}» есть у клиента, а в каталоге сервера (ReviewMarks) — нет."),
        ];
        if (problems.Count == 0 && !server.SequenceEqual(client))
            problems.Add($"Коды те же, но порядок другой: сервер — {string.Join(", ", server)}; клиент — {string.Join(", ", client)}.");
        return problems;
    }

    private static List<string> ClientCodes()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ClientFile));
        var block = Regex.Match(text, @"WORK_MARK_CODES\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline);
        if (!block.Success) return [];
        return [.. Regex.Matches(block.Groups["body"].Value, @"'(?<code>[^']+)'").Select(m => m.Groups["code"].Value)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory + " — тест сверяет коды клиента.");
    }
}
