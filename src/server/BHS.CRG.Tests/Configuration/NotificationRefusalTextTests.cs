using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// В колокольчик уходит текст, написанный для человека, — а не сообщение чужого исключения
/// (правило issue #691, дверь найдена ревью PR #1049).
///
/// <para>Зачем сторож. Уведомление — такой же выход наружу, как ответ на запрос, и правило у них
/// обязано быть одно: дословно доходит только наш отказ, чужое заменяется общим текстом, а
/// подробности идут в журнал. У ответа это правило проверено (<see cref="ApiErrorMappingTests" />) и
/// держится конвейером — одним местом на все запросы. У колокольчика общего места нет: текст
/// собирает каждый вызов сам, и <c>ex.Message</c> в нём выглядит как забота о пользователе.</para>
///
/// <para>Найдено на живом примере: два вызова из двадцати двух передавали сообщение чужого
/// исключения дословно — генерация документа и распознавание. Через первый уезжал вывод компилятора
/// шаблона с путями папки прогона, через второй — ответ стороннего сервиса вместе с его адресом.
/// Обе двери закрыты, но закрыты ПООДИНОЧКЕ: следующий вызов напишут так же, и компилятор не
/// возразит. Поэтому проверка смотрит на форму вызова, а не на нынешний состав.</para>
///
/// <para>Санкционированная форма — <c>Refusals.TextOr(ex, «общий текст»)</c>: она и отделяет наш
/// отказ от чужого, и заставляет автора придумать этот общий текст.</para>
/// </summary>
public class NotificationRefusalTextTests
{
    private static readonly string[] Projects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    /// <summary>
    /// Осознанные исключения: имя файла → причина. Пустой список — не признак, что правило лишнее:
    /// он ровно то, к чему стремились. Добавляя строку, вы принимаете решение.
    /// </summary>
    private static readonly Dictionary<string, string> Deliberate = new(StringComparer.Ordinal);

    [Fact]
    public void A_notification_never_carries_a_foreign_exception_message()
    {
        var offenders = new List<string>();

        foreach (var project in Projects)
        {
            var root = Path.Combine(SolutionDir, project);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var name = Path.GetFileName(file);
                if (Deliberate.ContainsKey(name)) continue;

                var text = File.ReadAllText(file);
                foreach (var call in PublishCalls(text))
                    if (MessageAccess.IsMatch(call))
                        offenders.Add($"{name}: {Shorten(call)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Уведомление несёт сообщение чужого исключения:\n  " + string.Join("\n  ", offenders) + "\n\n" +
            "Через колокольчик так уезжают вывод компилятора шаблона с путями папки прогона, текст\n" +
            "Npgsql со строкой подключения и ответы сторонних сервисов вместе с их адресами. Оберните\n" +
            "текст в Refusals.TextOr(ex, «общий текст»): наш отказ дойдёт дословно, чужой — заменится,\n" +
            "а исключение целиком запишет конвейер. Если сообщение здесь заведомо наше, впишите файл\n" +
            "в Deliberate с причиной — это решение, а не формальность.");
    }

    /// <summary>
    /// Аргументы каждого вызова <c>PublishAsync(</c> — по балансу скобок, а не построчно: текст
    /// уведомления почти всегда перенесён на следующие строки, и построчная проверка не увидела бы
    /// ровно те вызовы, ради которых написана.
    /// </summary>
    private static IEnumerable<string> PublishCalls(string text)
    {
        const string marker = "PublishAsync(";
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) yield break;

            var start = at + marker.Length;
            var depth = 1;
            var i = start;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }

            yield return text[start..(depth == 0 ? i - 1 : text.Length)];
            from = i;
        }
    }

    // Обращение к .Message у переменной исключения. Refusals.TextOr(ex, …) под правило не попадает
    // и потому остаётся единственной разрешённой формой.
    private static readonly Regex MessageAccess = new(
        @"\b(ex|e|error|exception)\w*\.Message\b", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string Shorten(string call)
    {
        var flat = Regex.Replace(call, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
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
