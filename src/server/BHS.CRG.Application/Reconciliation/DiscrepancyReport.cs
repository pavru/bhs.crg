using System.Globalization;
using System.Text.Json;
using BHS.CRG.Domain.Reconciliation;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>Готовый лист отчёта, независимый от библиотеки выгрузки.</summary>
public record ReportSheet(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    IReadOnlyList<string> Preamble);

/// <summary>
/// «Отчёт о расхождениях» (issue #444) — тот самый артефакт, который сегодня ведут руками.
///
/// Сборка листов вынесена из инфраструктуры: она вся про смысл (какие колонки, как назвать статус,
/// что написать в шапке), а не про NPOI, и проверяется без файлов на диске.
/// </summary>
public static class DiscrepancyReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly Dictionary<FindingStatus, string> FindingStatusLabels = new()
    {
        [FindingStatus.Match] = "Совпадает",
        [FindingStatus.Mismatch] = "Расхождение",
        [FindingStatus.MissingLeft] = "Нет слева",
        [FindingStatus.MissingRight] = "Нет справа",
    };

    private static readonly Dictionary<DecisionKind, string> DecisionLabels = new()
    {
        [DecisionKind.Accepted] = "Признано нормой",
        [DecisionKind.Suppressed] = "Исключено из сверки",
    };

    private static readonly Dictionary<ObservationSeverity, string> SeverityLabels = new()
    {
        [ObservationSeverity.Error] = "Существенно",
        [ObservationSeverity.Warning] = "Внимание",
        [ObservationSeverity.Info] = "К сведению",
    };

    private static readonly Dictionary<ObservationStatus, string> ObservationStatusLabels = new()
    {
        [ObservationStatus.New] = "Не разобрано",
        [ObservationStatus.Confirmed] = "Подтверждено",
        [ObservationStatus.Rejected] = "Отклонено",
    };

    private static string Num(double? v) =>
        v is null ? "" : (Math.Round(v.Value, 3)).ToString(Ru);

    /// <summary>
    /// Находки сверки. Разница считается здесь, а не оставляется читателю: отчёт смотрят глазами, и
    /// вычитание в уме по сотне строк — ровно то, от чего этот файл должен избавить.
    /// </summary>
    public static ReportSheet Findings(
        string title, ReconciliationRun? run,
        IReadOnlyList<FindingView> findings)
    {
        var preamble = new List<string> { title };
        if (run is null)
        {
            preamble.Add("Прогонов не было.");
        }
        else if (run.Status == ReconciliationRunStatus.Failed)
        {
            // Пустая таблица без объяснения читается как «расхождений нет» — самое опасное
            // недоразумение в подсистеме, и в выгрузке оно опаснее всего.
            preamble.Add($"ПРОГОН НЕ ВЫПОЛНЕН: {run.Error}");
        }
        else
        {
            preamble.Add($"Прогон: {run.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
            preamble.Add($"Совпало: {run.MatchCount} · расхождений: {run.MismatchCount} · "
                       + $"нет слева: {run.MissingLeftCount} · нет справа: {run.MissingRightCount}");
        }

        var rows = findings.Select(v =>
        {
            var f = v.Finding;
            var diff = f.LeftValue is { } l && f.RightValue is { } r ? Num(l - r) : "";
            return (IReadOnlyList<string?>)
            [
                FindingStatusLabels[f.Status],
                v.Resolved ? "да" : "",
                f.Label,
                Num(f.LeftValue),
                Num(f.RightValue),
                diff,
                Provenance(f.Provenance),
                v.Decision is null ? "" : DecisionLabels[v.Decision.Kind],
                v.Decision?.Note ?? "",
                v.Decision?.DecidedBy ?? "",
            ];
        }).ToList();

        return new ReportSheet("Сверка",
            ["Статус", "Устранено", "Позиция", "Слева", "Справа", "Разница",
             "Где смотреть", "Решение", "Примечание", "Кто разобрал"],
            rows, preamble);
    }

    /// <summary>
    /// Замечания внешнего анализа. Происхождение написано в шапке листа: файл уходит в переписку и на
    /// совещания, и там утверждение агента не должно сойти за результат проверки.
    /// </summary>
    public static ReportSheet Observations(IReadOnlyList<AgentObservation> items)
    {
        var preamble = new List<string>
        {
            "Замечания внешнего ИИ-анализа",
            "Это утверждения агента, а НЕ результат проверки системы. Каждое требует подтверждения человеком.",
        };

        var rows = items.Select(o => (IReadOnlyList<string?>)
        [
            SeverityLabels[o.Severity],
            ObservationStatusLabels[o.Status],
            o.Title,
            o.Detail ?? "",
            References(o.References),
            o.ReviewNote ?? "",
            o.ReviewedBy ?? "",
            o.ReportedBy ?? "",
        ]).ToList();

        return new ReportSheet("Замечания",
            ["Существенность", "Разбор", "Суть", "Подробности", "Где смотреть",
             "Примечание", "Кто разобрал", "Кто сообщил"],
            rows, preamble);
    }

    /// <summary>Провенанс находки человеческим текстом: голый JSON в отчёте бесполезен.</summary>
    private static string Provenance(JsonDocument provenance)
    {
        var parts = new List<string>();
        foreach (var side in (string[])["left", "right"])
        {
            if (!provenance.RootElement.TryGetProperty(side, out var s)
                || s.ValueKind != JsonValueKind.Object) continue;

            var column = s.TryGetProperty("column", out var c) ? c.GetString() : null;
            var rows = s.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.GetArrayLength() : 0;
            parts.Add($"{(side == "left" ? "слева" : "справа")}: {column}, строк {rows}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>Ссылки замечания человеческим текстом.</summary>
    private static string References(JsonDocument references)
    {
        var root = ObservationReferences.Unwrap(references.RootElement);
        if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? "";
        if (root.ValueKind != JsonValueKind.Object) return root.GetRawText();

        var parts = new List<string>();
        if (root.TryGetProperty("documentIds", out var docs) && docs.ValueKind == JsonValueKind.Array)
            parts.Add($"документов: {docs.GetArrayLength()}");
        if (root.TryGetProperty("sourceId", out var src) && src.ValueKind == JsonValueKind.String)
        {
            var rows = root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array
                ? $", строк {r.GetArrayLength()}" : "";
            parts.Add($"источник {src.GetString()?[..8]}{rows}");
        }
        if (root.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
            parts.Add(note.GetString() ?? "");
        return string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
