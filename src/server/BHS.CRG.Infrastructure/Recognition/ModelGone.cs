using System.Net;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Как по ответу поставщика понять, что модели у него больше нет, — одним местом для пробы каталога
/// (<c>RecognitionModelCatalog</c>) и для движков распознавания (issue #923). Правило живёт отдельно
/// потому, что читать его должны оба: разъедься они, проба называла бы модель снятой, а распознавание
/// с тем же ответом — просто недоступной, или наоборот.
/// </summary>
public static partial class ModelGone
{
    /// <summary>
    /// «Нет такой модели» — ТОЛЬКО 404. Всё остальное (кончились деньги, лимит, ключ отозван, сеть
    /// молчит) — не приговор модели: объявить её снятой из-за пустого счёта значит отправить
    /// пользователя менять то, что работает. У Anthropic пустой счёт даёт 400 и на выдуманное имя
    /// модели, то есть маскирует 404 — сигнал тогда просто не приходит, и это безопасная сторона.
    /// </summary>
    public static bool Is(HttpStatusCode status) => status == HttpStatusCode.NotFound;

    /// <summary>
    /// Совет поставщика из текста отказа (открыт ради теста: разбор чужого сообщения — то, что ломается
    /// молча при смене формулировки). Google в ответе 404 прямо называет замену
    /// («Please update your code to use models/gemini-3.5-flash-lite…») — это самое полезное, что есть
    /// в сообщении, и терять его, оставив сухое «модель недоступна», было бы расточительством.
    /// </summary>
    public static string? AdviceFrom(string body)
    {
        var m = SuggestedModel().Match(body);
        return m.Success ? $"поставщик рекомендует {m.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"use\s+models/([A-Za-z0-9.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SuggestedModel();
}
