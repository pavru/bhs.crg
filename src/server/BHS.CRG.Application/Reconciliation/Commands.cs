using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

// ── Определения ─────────────────────────────────────────────────────────────

public record ListReconciliationsQuery(CatalogScope? Scope, Guid? ScopeId)
    : IRequest<IReadOnlyList<ReconciliationDefinition>>;

public record GetReconciliationQuery(Guid Id) : IRequest<ReconciliationDefinition?>;

public record CreateReconciliationCommand(string Name, CatalogScope Scope, Guid? ScopeId, JsonDocument Spec)
    : IRequest<ReconciliationDefinition>;

public record UpdateReconciliationCommand(Guid Id, string Name, JsonDocument Spec)
    : IRequest<ReconciliationDefinition>;

public record DeleteReconciliationCommand(Guid Id) : IRequest;

// ── Прогоны ─────────────────────────────────────────────────────────────────

/// <summary>
/// Прогон синхронный: сверка читает уже сохранённые данные источников и распознавание НЕ запускает
/// (P5 в issue #414). Фоновая задача понадобится, только если появятся тяжёлые своды.
/// </summary>
public record RunReconciliationCommand(Guid DefinitionId) : IRequest<ReconciliationRun>;

public record ListReconciliationRunsQuery(Guid DefinitionId, int Limit = 20)
    : IRequest<IReadOnlyList<ReconciliationRun>>;

/// <summary>
/// Находки прогона с наложенным решением и вычисленным признаком устранения.
/// </summary>
/// <param name="RunId">Пусто — берётся последний завершённый прогон определения.</param>
public record ListFindingsQuery(Guid DefinitionId, Guid? RunId = null)
    : IRequest<IReadOnlyList<FindingView>>;

// ── Решения ─────────────────────────────────────────────────────────────────

/// <summary>
/// Решение адресуется КЛЮЧОМ, а не идентификатором находки: находка принадлежит прогону и умирает
/// вместе с ним, а решение обязано пережить любое число прогонов. Приняв findingId, эндпоинт молча
/// терял бы память при следующем прогоне — ровно та ошибка, от которой защищает модель (#414).
/// </summary>
public record SetDecisionCommand(Guid DefinitionId, string Key, DecisionKind Kind, string? Note, string? DecidedBy)
    : IRequest<ReconciliationDecision>;

public record RemoveDecisionCommand(Guid DefinitionId, string Key) : IRequest;
