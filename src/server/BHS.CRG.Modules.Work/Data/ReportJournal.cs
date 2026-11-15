using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Состояние отчёта для сравнения «до и после» (change <c>work-14-report-journal</c>): значения полей шапки и строк по
/// ключу «строка, поле». Снимается ДО правки — правка меняет отчёт на месте.
/// </summary>
public sealed class ReportSnapshot
{
    internal ReportSnapshot(IReadOnlyDictionary<(Guid? Line, string Field), JsonNode?> values, IReadOnlyList<Guid?> order)
    {
        Values = values;
        Order = order;
    }

    internal IReadOnlyDictionary<(Guid? Line, string Field), JsonNode?> Values { get; }

    /// <summary>Порядок строк для порядка записей «что изменилось»: шапка первой, затем строки по позиции.</summary>
    internal IReadOnlyList<Guid?> Order { get; }
}

/// <summary>
/// Единственное место записи журнала отчёта (ТЗ WORK-11, WORK-12, WORK-31, WORK-51; change
/// <c>work-14-report-journal</c>). Вызывает его путь сохранения отчёта (<c>ReportDesk</c>): запись ложится одним
/// сохранением с записью протокола того же номера и в той же транзакции, что и правка, — сбой сохранения не оставляет
/// ни правки, ни записи. Второе место записи ловит <c>WorkJournalInventoryTests</c>.
///
/// <para><b>Что изменилось</b> — только изменённые поля, по ключу «строка, поле»: шапка — <c>line: null</c>, сведения
/// строки — по ключам, без копии всего JSON. Заведение кладёт названные поля с <c>before: null</c>, удаление — все поля
/// с <c>after: null</c>: свидетельство того, что было удалено.</para>
///
/// <para><b>Правка после подачи</b> (ТЗ WORK-11) — время действия позже момента подачи. Время действия — время
/// устройства (заявление, MOB-17), а не названо — время сервера: правка с экрана без времени устройства после подачи
/// тоже правка после подачи. На этой пометке держится только пометка, а не отказ; расхождение времён показывает очередь
/// приёмки (<c>work-16</c>).</para>
/// </summary>
public sealed class ReportJournal(WorkDbContext db)
{
    /// <summary>Снять состояние отчёта — до правки или удаления. Строки обязаны быть загружены.</summary>
    public static ReportSnapshot Capture(Report report)
    {
        var values = new Dictionary<(Guid? Line, string Field), JsonNode?>
        {
            [(null, "constructionId")] = report.ConstructionId.ToString(),
            [(null, "date")] = report.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [(null, "shift")] = Report.ShiftCode(report.Shift),
            [(null, "hours")] = Number(report.Hours),
            [(null, "note")] = report.Note,
        };
        var lines = report.Lines.OrderBy(l => l.Position).ToList();
        foreach (var line in lines)
        {
            Guid? id = line.Id;
            values[(id, "position")] = line.Position;
            values[(id, "workTypeId")] = line.WorkTypeId.ToString();
            values[(id, "variantId")] = line.VariantId?.ToString();
            values[(id, "volume")] = Number(line.Volume);
            values[(id, "unitId")] = line.UnitId.ToString();
            values[(id, "sectionId")] = line.SectionId?.ToString();
            values[(id, "location")] = line.Location;
            values[(id, "comment")] = line.Comment;
            values[(id, "isCrew")] = line.IsCrew;
            values[(id, "materials")] = new JsonArray([
                .. line.Materials.OrderBy(m => m.Position)
                    .Select(m => (JsonNode)new JsonObject { ["nomenclatureId"] = m.NomenclatureId.ToString(), ["note"] = m.Note }),
            ]);
            values[(id, "participants")] = new JsonArray([
                .. line.Participants.Select(p => p.EmployeeId).Order().Select(e => (JsonNode)e.ToString()),
            ]);
            if (JsonNode.Parse(line.Details) is JsonObject details)
                foreach (var (key, value) in details)
                    values[(id, "details." + key)] = value?.DeepClone();
        }
        return new ReportSnapshot(values, [null, .. lines.Select(l => (Guid?)l.Id)]);
    }

    /// <summary>
    /// Добавить запись журнала в контекст — без сохранения: сохраняет вызывающий, одним сохранением с протоколом.
    /// <paramref name="before" /> — состояние до действия (у заведения — <c>null</c>); состояние после снимается с
    /// <paramref name="report" />, у удаления его нет.
    /// </summary>
    public ReportJournalEntry Append(
        string kind, Report report, ReportSnapshot? before, ReportJournalActor author, string executorName, Guid changeId,
        long seq, DateTimeOffset? deviceTime, DateTimeOffset serverTime)
    {
        var after = kind == ReportJournalKinds.Deleted ? null : Capture(report);
        var actedAt = deviceTime ?? serverTime;
        var entry = ReportJournalEntry.Of(report, kind, changeId, seq, author, executorName, deviceTime, serverTime,
            afterSubmission: report.SubmittedAt is { } submitted && actedAt > submitted,
            changes: Diff(before, after).ToJsonString());
        db.Add(entry);
        return entry;
    }

    /// <summary>Изменённые поля: шапка первой, строки — по позиции «после», удалённые — следом, по позиции «до».</summary>
    internal static JsonArray Diff(ReportSnapshot? before, ReportSnapshot? after)
    {
        var was = before?.Values ?? new Dictionary<(Guid? Line, string Field), JsonNode?>();
        var now = after?.Values ?? new Dictionary<(Guid? Line, string Field), JsonNode?>();
        var order = (after?.Order ?? []).Concat(before?.Order ?? []).Distinct().ToList();
        var keys = now.Keys.Concat(was.Keys).Distinct()
            .OrderBy(k => order.IndexOf(k.Line)).ToList();

        var changes = new JsonArray();
        foreach (var key in keys)
        {
            var old = was.GetValueOrDefault(key);
            var fresh = now.GetValueOrDefault(key);
            if (JsonNode.DeepEquals(old, fresh)) continue;
            changes.Add(new JsonObject
            {
                ["line"] = key.Line?.ToString(), ["field"] = key.Field,
                ["before"] = old?.DeepClone(), ["after"] = fresh?.DeepClone(),
            });
        }
        return changes;
    }

    /// <summary>Изменённые поля записи через запятую, без повторов — для журнала ядра: «volume, hours».</summary>
    public static string FieldsOf(ReportJournalEntry entry) =>
        JsonNode.Parse(entry.Changes) is JsonArray changes
            ? string.Join(", ", changes.Select(c => c?["field"]?.GetValue<string>()).OfType<string>().Distinct())
            : "";

    /// <summary>
    /// Число без хвостовых нулей: колонка <c>numeric(18,4)</c> читается как <c>120.0000</c>, а запрос приносит
    /// <c>150</c> — без приведения журнал показал бы «120.0000 → 150».
    /// </summary>
    private static JsonNode? Number(decimal? value) =>
        value is { } v ? JsonValue.Create(v / 1.0000000000000000000000000000m) : null;
}

/// <summary>Запись журнала для ответа чтения: «что изменилось» — разобранным массивом, а не строкой.</summary>
public static class ReportJournalView
{
    public static object Of(ReportJournalEntry e)
    {
        using var changes = JsonDocument.Parse(e.Changes);
        return new
        {
            seq = e.Seq, kind = e.Kind, changeId = e.ChangeId,
            author = new { id = e.AuthorId, name = e.AuthorName },
            executor = new { employeeId = e.ExecutorEmployeeId, name = e.ExecutorName },
            enteredFor = e.EnteredFor, deviceTime = e.DeviceTime, serverTime = e.ServerTime,
            afterSubmission = e.AfterSubmission, changes = changes.RootElement.Clone(),
        };
    }
}
