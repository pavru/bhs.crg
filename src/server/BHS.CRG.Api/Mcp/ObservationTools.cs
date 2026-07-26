using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <param name="Status">New — ещё не разобрано человеком; Confirmed / Rejected — разобрано.</param>
/// <param name="ReviewNote">Что ответил человек. Отклонение с причиной ценнее молчаливого.</param>
public record ObservationInfo(
    Guid Id, string Scope, Guid? ScopeId, string Key, string Title, string? Detail,
    string Severity, string Status, JsonElement References,
    string? ReportedBy, string? ReviewedBy, string? ReviewNote, DateTimeOffset UpdatedAt);

/// <summary>
/// Журнал замечаний внешнего анализа (issue #440). До него находки агента жили только в переписке:
/// память не копилась, отчёт не с чем было сравнить.
///
/// Замечание — НЕ результат системы, а утверждение агента, требующее подтверждения человеком.
/// Подтвердить или отклонить через MCP нельзя намеренно: агент не подтверждает сам себя — прямое
/// следствие «предложить → подтвердить → персистить» из issue #414.
/// </summary>
[McpServerToolType]
public class ObservationTools(IMediator mediator, IHttpContextAccessor http)
{
    private string? CurrentUser =>
        http.HttpContext?.User is { } u
            ? u.FindFirst("displayName")?.Value ?? u.FindFirstValue(ClaimTypes.Email)
            : null;

    private static ObservationInfo ToInfo(AgentObservation o) => new(
        o.Id, o.Scope.ToString(), o.ScopeId, o.Key, o.Title, o.Detail,
        o.Severity.ToString(), o.Status.ToString(), o.References.RootElement.Clone(),
        o.ReportedBy, o.ReviewedBy, o.ReviewNote, o.UpdatedAt);

    [McpServerTool(Name = "list_observations", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Замечания анализа")]
    [Description("""
        Ранее сообщённые замечания с их разбором. Смотрите ПЕРЕД тем, как сообщать новые: повтор уже
        разобранного возвращает в работу то, что человек закрыл, и обесценивает журнал.

        Отклонённые замечания читайте особенно внимательно — в reviewNote написано, почему это не
        ошибка, и повторять её не нужно.
        """)]
    public async Task<IReadOnlyList<ObservationInfo>> ListObservationsAsync(
        CancellationToken ct,
        [Description("Область: System, Construction, Section, Set.")] string? scope = null,
        [Description("Идентификатор области.")] Guid? scopeId = null,
        [Description("Фильтр разбора: New, Confirmed, Rejected.")] string? status = null)
    {
        CatalogScope? s = Enum.TryParse<CatalogScope>(scope, true, out var sv) ? sv : null;
        ObservationStatus? st = Enum.TryParse<ObservationStatus>(status, true, out var stv) ? stv : null;
        var items = await mediator.Send(new ListObservationsQuery(s, scopeId, st), ct);
        return [.. items.Select(ToInfo)];
    }

    [McpServerTool(Name = "report_observation", ReadOnly = false, Idempotent = true, Destructive = false,
        Title = "Сообщить замечание")]
    [Description("""
        Записать находку анализа в журнал системы, чтобы она не потерялась вместе с этим разговором.

        Замечание попадает в систему как УТВЕРЖДЕНИЕ, требующее подтверждения человеком, а не как
        результат проверки. Подтвердить его вы не можете — это решение человека.

        Ключ (key) обязан быть устойчивым к повторному анализу: постройте его из существа утверждения
        (например «аоср-5.организация-не-совпадает-с-реестром»), а НЕ из номера строки или порядка
        обхода. Повторное сообщение с тем же ключом обновляет запись; нестабильный ключ забьёт журнал
        дублями, и он перестанет быть памятью.

        Ссылки (references) обязательны: идентификаторы документов, источник со строками — то, по чему
        человек проверит утверждение глазами. Утверждение без опоры это мнение, а не находка.
        """)]
    public async Task<ObservationInfo> ReportObservationAsync(
        [Description("Идентификатор комплекта документов, к которому относится замечание.")]
        Guid setId,
        [Description("Устойчивый ключ утверждения — см. описание инструмента.")]
        string key,
        [Description("Суть одной строкой.")]
        string title,
        [Description("""
            На что опирается утверждение. Объект вида
            {"documentIds":["…"],"sourceId":"…","rows":[3,7],"note":"…"} — по нему человек проверит
            находку глазами.
            """)]
        JsonElement references,
        CancellationToken ct,
        [Description("Подробности: что с чем не сходится и почему это важно.")] string? detail = null,
        [Description("Существенность: Info, Warning, Error. По умолчанию Warning.")] string? severity = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new McpException("Ключ обязателен: без него повторный анализ создаст дубль.");
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("Суть замечания обязательна.");
        if (references.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || (references.ValueKind == JsonValueKind.Object && !references.EnumerateObject().Any()))
            throw new McpException(
                "Ссылки обязательны: замечание без адреса непроверяемо. Укажите документы либо источник со строками.");

        var sev = Enum.TryParse<ObservationSeverity>(severity, true, out var sv)
            ? sv : ObservationSeverity.Warning;

        var observation = await mediator.Send(new ReportObservationCommand(
            CatalogScope.Set, setId, key.Trim(), title.Trim(), detail,
            sev, JsonDocument.Parse(references.GetRawText()), CurrentUser), ct);

        return ToInfo(observation);
    }
}
