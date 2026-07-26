using System.ComponentModel;
using System.Security.Claims;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Reconciliation;
using MediatR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

public record ReconciliationInfo(Guid Id, string Name, string Scope, Guid? ScopeId);

/// <param name="Key">Нормализованный доменный ключ позиции. Именно его передают в propose_alias —
/// написанный от руки не совпадёт ни с чем.</param>
/// <param name="Status">Match / Mismatch / MissingLeft / MissingRight.</param>
/// <param name="Resolved">Было расхождение в прошлом прогоне, сейчас совпадение.</param>
public record FindingInfo(
    string Key, string Label, double? LeftValue, double? RightValue,
    string Status, bool Resolved, string? Decision, string? DecisionNote);

/// <param name="Status">Proposed — ждёт человека; Confirmed — применяется в сверке; Rejected — отказ.</param>
public record AliasInfo(
    Guid Id, string AliasKey, string AliasLabel, string CanonicalKey, string CanonicalLabel,
    string Status, string? Note, string? ProposedBy, string? ConfirmedBy);

/// <summary>
/// Сверка для внешнего агента (issue #448, вторая часть Ф2 из #414).
///
/// Агент читает находки и предлагает пары для несопоставленных позиций. Подтверждает ТОЛЬКО человек —
/// инструмента подтверждения здесь нет и не будет: неподтверждённый алиас в пути сравнения означал бы
/// модель внутри арифметики и «прыгающий» от прогона к прогону отчёт (риск P1).
/// </summary>
[McpServerToolType]
public class ReconciliationTools(IMediator mediator, IHttpContextAccessor http)
{
    private string? CurrentUser =>
        http.HttpContext?.User is { } u
            ? u.FindFirst("displayName")?.Value ?? u.FindFirstValue(ClaimTypes.Email)
            : null;

    private static AliasInfo ToInfo(ReconciliationAlias a) => new(
        a.Id, a.AliasKey, a.AliasLabel, a.CanonicalKey, a.CanonicalLabel,
        a.Status.ToString(), a.Note, a.ProposedBy, a.ConfirmedBy);

    [McpServerTool(Name = "list_reconciliations", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Сверки")]
    [Description("Настроенные сверки — точка входа: их идентификаторы нужны для чтения находок.")]
    public async Task<IReadOnlyList<ReconciliationInfo>> ListReconciliationsAsync(CancellationToken ct)
        => [.. (await mediator.Send(new ListReconciliationsQuery(null, null), ct))
            .Select(d => new ReconciliationInfo(d.Id, d.Name, d.Scope.ToString(), d.ScopeId))];

    [McpServerTool(Name = "get_reconciliation_findings", ReadOnly = true, Idempotent = true,
        Destructive = false, Title = "Находки сверки")]
    [Description("""
        Находки последнего прогона: что сошлось, что разошлось и какие позиции не нашли пары на другой
        стороне (MissingLeft / MissingRight).

        Это результат АРИФМЕТИКИ системы, а не оценка — цитируйте его как есть.

        Несопоставленные позиции и есть кандидаты на связывание: если две из них называются по-разному,
        но означают одно и то же, предложите пару через propose_alias, взяв ключи ОТСЮДА.
        """)]
    public async Task<IReadOnlyList<FindingInfo>> GetFindingsAsync(
        [Description("Идентификатор сверки.")] Guid reconciliationId, CancellationToken ct)
        => [.. (await mediator.Send(new ListFindingsQuery(reconciliationId), ct))
            .Select(v => new FindingInfo(
                v.Finding.Key, v.Finding.Label, v.Finding.LeftValue, v.Finding.RightValue,
                v.Finding.Status.ToString(), v.Resolved,
                v.Decision?.Kind.ToString(), v.Decision?.Note))];

    [McpServerTool(Name = "list_aliases", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Алиасы позиций")]
    [Description("""
        Уже заведённые соответствия позиций с их разбором. Смотрите ПЕРЕД тем, как предлагать новые.

        Отклонённое предложение — это ответ человека, а не повод повторить: он уже решил, что позиции
        разные. Повтор обесценивает список и тратит его внимание.
        """)]
    public async Task<IReadOnlyList<AliasInfo>> ListAliasesAsync(CancellationToken ct)
        => [.. (await mediator.Send(new ListAliasesQuery(null), ct)).Select(ToInfo)];

    [McpServerTool(Name = "propose_alias", ReadOnly = false, Idempotent = true, Destructive = false,
        Title = "Предложить соответствие позиций")]
    [Description("""
        Предложить, что две по-разному названные позиции — одно и то же (например «Hyperline CM-1U-ML»
        и «Органайзер СвязьСтройДеталь»).

        Создаётся ПРЕДЛОЖЕНИЕ. На сверку оно не влияет, пока человек его не подтвердит, и подтвердить
        его вы не можете — такого инструмента нет.

        Ключи берите из get_reconciliation_findings: они нормализованные, и написанные от руки просто
        не совпадут ни с чем. Наименования передавайте те, что видит человек, — по голым ключам он
        предложение не разберёт.

        В примечании объясните, почему считаете позиции одинаковыми: без объяснения человеку придётся
        выяснять это заново, и предложение скорее отклонят, чем разберут.
        """)]
    public async Task<AliasInfo> ProposeAliasAsync(
        [Description("Ключ позиции, которую сводим (из находок).")] string aliasKey,
        [Description("Её наименование, как видит человек.")] string aliasLabel,
        [Description("Ключ позиции, к которой сводим (из находок).")] string canonicalKey,
        [Description("Её наименование, как видит человек.")] string canonicalLabel,
        CancellationToken ct,
        [Description("Почему это одно и то же.")] string? note = null)
    {
        if (string.IsNullOrWhiteSpace(aliasKey) || string.IsNullOrWhiteSpace(canonicalKey))
            throw new McpException("Нужны оба ключа — возьмите их из get_reconciliation_findings.");
        if (string.Equals(aliasKey, canonicalKey, StringComparison.Ordinal))
            throw new McpException("Это одна и та же позиция — связывать нечего.");

        var alias = await mediator.Send(new ProposeAliasCommand(
            aliasKey.Trim(),
            string.IsNullOrWhiteSpace(aliasLabel) ? aliasKey.Trim() : aliasLabel.Trim(),
            canonicalKey.Trim(),
            string.IsNullOrWhiteSpace(canonicalLabel) ? canonicalKey.Trim() : canonicalLabel.Trim(),
            note, CurrentUser,
            // Агент НИКОГДА не подтверждает собственное предложение — это решение человека.
            Confirm: false), ct);

        return ToInfo(alias);
    }
}
