using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BHS.CRG.Application.Support;

/// <summary>
/// Заготовка текста issue из сообщения пользователя и техблока (issue #834).
///
/// Заготовка, а не итог: администратор правит её перед отправкой — из слов пользователя убирается
/// внутреннее (названия строек, организаций, объектов), потому что первый читатель текста в
/// публичном репозитории — интернет. Поэтому же здесь НЕТ имени и адреса автора: у нас они есть в
/// записи, а наружу личные данные не уходят.
///
/// Функция чистая и живёт в Application, а не в форме и не в сервисе: заготовку показывают ДО
/// правки и собирают заново, если правку стёрли, — и то и другое должно давать один и тот же текст.
/// </summary>
public static class BugReportIssueText
{
    /// <param name="message">«Что произошло» словами автора.</param>
    /// <param name="tech">Техблок как прислал клиент; <c>null</c> — раздела не будет.</param>
    /// <param name="hasScreenshot">Есть ли снимок экрана: он остаётся у администратора, но знать,
    /// что он существует, разработчику полезно — можно попросить.</param>
    public static string Build(string message, JsonElement? tech, bool hasScreenshot)
    {
        var sb = new StringBuilder();
        sb.Append(message.Trim());
        sb.Append("\n\n## Техническая информация\n\n");

        var lines = new List<string>();
        if (tech is { ValueKind: JsonValueKind.Object } t)
        {
            // Порознь и с честными подписями: у SPA своего номера версии нет — она показывает
            // тот, что получила от сервера ПРИ ЗАГРУЗКЕ вкладки. Вкладка живёт неделями и
            // переживает обновление сервера, поэтому расхождение этих двух строк само по себе
            // объясняет часть сообщений («у меня всё по-старому»).
            Add(lines, "Версия при загрузке страницы", Version(t));
            Add(lines, "Версия сервера сейчас", Version(Child(t, "server")));
            Add(lines, "Экран", Str(t, "route"));
            Add(lines, "Браузер", Str(t, "userAgent"));
            Add(lines, "Окно", Str(t, "viewport"));
        }
        if (hasScreenshot) lines.Add("- Снимок экрана: у администратора (в issue не передаётся)");

        sb.Append(lines.Count > 0 ? string.Join("\n", lines) : "_Клиент не прислал техблок._");

        if (tech is { ValueKind: JsonValueKind.Object } t2)
        {
            AppendApiErrors(sb, t2);
            AppendStack(sb, t2);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>«0.143.0 (сборка a1b2c3d)» — из полей version/commit того же объекта.</summary>
    private static string? Version(JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Object } o) return null;
        var version = Str(o, "version");
        if (version is null) return null;
        var commit = Str(o, "commit");
        return commit is null ? version : $"{version} (сборка {commit})";
    }

    private static void AppendApiErrors(StringBuilder sb, JsonElement tech)
    {
        if (!tech.TryGetProperty("apiErrors", out var errors) || errors.ValueKind != JsonValueKind.Array) return;
        var rows = new List<string>();
        foreach (var e in errors.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var status = e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32().ToString(CultureInfo.InvariantCulture)
                : "—";
            rows.Add($"| {Str(e, "at") ?? "—"} | {Str(e, "method") ?? "—"} `{Str(e, "url") ?? "—"}` " +
                     $"| {status} | `{Str(e, "traceId") ?? "—"}` |");
        }
        if (rows.Count == 0) return;

        sb.Append("\n\n### Последние ошибки API\n\n");
        sb.Append("| Время | Запрос | Код | Идентификатор запроса |\n|---|---|---|---|\n");
        sb.Append(string.Join("\n", rows));
    }

    private static void AppendStack(StringBuilder sb, JsonElement tech)
    {
        var stack = Str(tech, "stack");
        if (stack is null) return;
        // Код-блоком: стек сплошным текстом в Markdown склеивается в абзац и становится нечитаемым.
        sb.Append("\n\n### Стек сбоя интерфейса\n\n```\n").Append(stack.Trim()).Append("\n```");
    }

    private static void Add(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add($"- {label}: {value}");
    }

    private static JsonElement? Child(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement? source, string name)
    {
        if (source is not { ValueKind: JsonValueKind.Object } o) return null;
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
