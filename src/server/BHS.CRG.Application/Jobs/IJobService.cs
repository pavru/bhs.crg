using BHS.CRG.Domain.Jobs;

namespace BHS.CRG.Application.Jobs;

/// <summary>Активная (или недавняя) фоновая задача для индикатора.</summary>
public record JobDto(
    Guid Id,
    string Kind,
    Guid TargetId,
    string Status,
    string Title,
    string? Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt);

/// <summary>
/// Постановка долгих операций в фон и запрос «мои активные задачи» для индикатора. Реализация ставит
/// запись Job(Queued) в БД (источник истины) и толкает id в in-process очередь, которую разбирает
/// hosted-сервис. Эндпоинт возвращает Id сразу (202), не держа реквест на время операции.
/// </summary>
public interface IJobService
{
    Task<Guid> EnqueueAsync(JobKind kind, Guid userId, Guid targetId, string title, string? payload, CancellationToken ct);

    /// <summary>Активные (Queued/Running) задачи пользователя — источник данных индикатора.</summary>
    Task<IReadOnlyList<JobDto>> GetActiveForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Есть ли у пользователя активная (Queued/Running) задача по данной цели — защита от дубля.</summary>
    Task<bool> HasActiveForTargetAsync(Guid userId, Guid targetId, CancellationToken ct);

    /// <summary>
    /// Есть ли активная задача такого вида — у КОГО УГОДНО, а не только у спросившего.
    ///
    /// Заведено для операций, у которых цель не объект, а система целиком (резервное копирование,
    /// issue #831): защита по цели там вырождается в <c>Guid.Empty</c>, а защита по владельцу не
    /// защищает вовсе — второй администратор о задаче первого не знает, потому что список активных
    /// показывает только свои.
    /// </summary>
    Task<bool> HasActiveOfKindAsync(JobKind kind, CancellationToken ct);

    /// <summary>Отменить свою задачу — ТОЛЬКО пока она в очереди (Queued). true — отменена; false —
    /// нельзя (уже выполняется/завершена/не найдена/чужая). Выполняемые добегают до конца.</summary>
    Task<bool> CancelAsync(Guid jobId, Guid userId, CancellationToken ct);
}
