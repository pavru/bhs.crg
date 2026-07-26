using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

public record ListObservationsQuery(CatalogScope? Scope, Guid? ScopeId, ObservationStatus? Status)
    : IRequest<IReadOnlyList<AgentObservation>>;

/// <summary>
/// Сообщить замечание. Повторное сообщение с тем же ключом в той же области — обновление, а не вторая
/// запись: иначе повторный анализ забил бы журнал дублями.
/// </summary>
public record ReportObservationCommand(
    CatalogScope Scope, Guid? ScopeId, string Key, string Title, string? Detail,
    ObservationSeverity Severity, JsonDocument References, string? ReportedBy)
    : IRequest<AgentObservation>;

/// <summary>Разбор замечания человеком. Через MCP недоступно — агент не подтверждает себя сам.</summary>
public record ReviewObservationCommand(Guid Id, ObservationStatus Status, string? Note, string? ReviewedBy)
    : IRequest<AgentObservation>;

/// <summary>
/// Агент отзывает собственное утверждение: перепроверил, условие больше не воспроизводится (#459).
/// Адресуется КЛЮЧОМ и областью, как и сообщение: идентификатора замечания у агента нет, да и не
/// должно быть — он мыслит утверждениями, а не записями.
/// </summary>
public record RetractObservationCommand(CatalogScope Scope, Guid? ScopeId, string Key, string? Note, string? By)
    : IRequest<AgentObservation?>;

public record DeleteObservationCommand(Guid Id) : IRequest;

public class ObservationHandlers(
    IRepository<AgentObservation> repo,
    IRepository<Domain.Documents.DocumentSet> sets,
    IRepository<Domain.Documents.Section> sections,
    INotificationService notifications) :
    IRequestHandler<ListObservationsQuery, IReadOnlyList<AgentObservation>>,
    IRequestHandler<ReportObservationCommand, AgentObservation>,
    IRequestHandler<ReviewObservationCommand, AgentObservation>,
    IRequestHandler<RetractObservationCommand, AgentObservation?>,
    IRequestHandler<DeleteObservationCommand>
{
    public async Task<IReadOnlyList<AgentObservation>> Handle(ListObservationsQuery q, CancellationToken ct)
    {
        var items = await repo.FindAsync(o =>
            (!q.Scope.HasValue || o.Scope == q.Scope.Value) &&
            (!q.ScopeId.HasValue || o.ScopeId == q.ScopeId.Value) &&
            (!q.Status.HasValue || o.Status == q.Status.Value), ct);

        // Неразобранные и более существенные — выше: журнал читают сверху вниз.
        return [.. items
            .OrderBy(o => o.Status == ObservationStatus.New ? 0 : 1)
            .ThenByDescending(o => (int)o.Severity)
            .ThenByDescending(o => o.UpdatedAt)];
    }

    public async Task<AgentObservation> Handle(ReportObservationCommand cmd, CancellationToken ct)
    {
        var existing = await FindByKeyAsync(cmd.Scope, cmd.ScopeId, cmd.Key, ct);

        AgentObservation observation;
        var isNew = existing is null;
        if (existing is null)
        {
            observation = AgentObservation.Create(cmd.Scope, cmd.ScopeId, cmd.Key, cmd.Title,
                cmd.Detail, cmd.Severity, cmd.References, cmd.ReportedBy);
            await repo.AddAsync(observation, ct);
        }
        else
        {
            observation = existing;
            observation.Report(cmd.Title, cmd.Detail, cmd.Severity, cmd.References, cmd.ReportedBy);
            repo.Update(observation);
        }

        await repo.SaveChangesAsync(ct);

        // Уведомляем только о НОВОМ и только о существенном (#457). Повтор с тем же ключом — это
        // обновление уже известного утверждения, и звонить о нём значит наказывать агента за
        // повторный анализ, ради которого стабильный ключ и вводился. Замечания рангом ниже видны
        // счётчиками; колокольчик — про «пришло что-то важное».
        if (isNew && cmd.Severity == ObservationSeverity.Error)
            await NotifyAsync(observation, ct);

        return observation;
    }

    public async Task<AgentObservation> Handle(ReviewObservationCommand cmd, CancellationToken ct)
    {
        var observation = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        observation.Review(cmd.Status, cmd.Note, cmd.ReviewedBy);
        repo.Update(observation);
        await repo.SaveChangesAsync(ct);
        return observation;
    }

    public async Task<AgentObservation?> Handle(RetractObservationCommand cmd, CancellationToken ct)
    {
        var observation = await FindByKeyAsync(cmd.Scope, cmd.ScopeId, cmd.Key, ct);
        if (observation is null) return null; // отзывать нечего — не ошибка, состояние уже такое

        observation.Retract(cmd.Note, cmd.By);
        repo.Update(observation);
        await repo.SaveChangesAsync(ct);
        return observation;
    }

    public async Task Handle(DeleteObservationCommand cmd, CancellationToken ct)
    {
        var observation = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        repo.Remove(observation);
        await repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Уведомление общесистемное, без адресата: замечание относится к КОМПЛЕКТУ, а не к тому, от чьего
    /// имени работал агент, — увидеть его должен тот, кто ведёт комплект.
    /// </summary>
    private async Task NotifyAsync(AgentObservation o, CancellationToken ct)
    {
        var link = await SetLinkAsync(o, ct);
        await notifications.PublishAsync(
            NotificationSeverity.Warning,
            "Внешний анализ: замечание",
            o.Title,
            source: "Сверка",
            userId: null,
            linkUrl: link,
            linkLabel: link is null ? null : "Открыть проблемы комплекта",
            ct: ct);
    }

    /// <summary>Ссылка ведёт в панель проблем комплекта; без стройки маршрут не собрать, поэтому
    /// поднимаемся по цепочке. Не собралась — уведомление всё равно нужно, просто без ссылки.</summary>
    private async Task<string?> SetLinkAsync(AgentObservation o, CancellationToken ct)
    {
        if (o.Scope != CatalogScope.Set || o.ScopeId is not { } setId) return null;
        var set = await sets.GetByIdAsync(setId, ct);
        if (set is null) return null;
        var section = await sections.GetByIdAsync(set.SectionId, ct);
        return section is null ? null
            : $"/document-sets/{section.ConstructionId}/sets/{setId}/issues";
    }

    /// <summary>Поиск по естественному ключу в два шага: <c>FindAsync</c> репозитория не отслеживает
    /// сущности, и правка полученной копии столкнулась бы с уже отслеживаемым экземпляром.</summary>
    private async Task<AgentObservation?> FindByKeyAsync(
        CatalogScope scope, Guid? scopeId, string key, CancellationToken ct)
    {
        var id = (await repo.FindAsync(o => o.Scope == scope && o.ScopeId == scopeId && o.Key == key, ct))
            .Select(o => (Guid?)o.Id).FirstOrDefault();
        return id is null ? null : await repo.GetByIdAsync(id.Value, ct);
    }
}
