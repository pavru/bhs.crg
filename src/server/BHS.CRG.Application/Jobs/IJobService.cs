using BHS.CRG.Domain.Jobs;

namespace BHS.CRG.Application.Jobs;

/// <summary>
/// Фоновая задача — для индикатора активных и для запроса «чем кончилось» по id.
///
/// <paramref name="Error" /> и <paramref name="FinishedAt" /> заполнены только у завершившейся
/// задачи и добавлены вместе с запросом по id (issue #898): индикатору они не нужны — он показывает
/// идущие, а итог человек читает уведомлением, — но снаружи, из MCP, задача иначе просто исчезает,
/// и успех неотличим от отказа. Причина отказа в системе была и до этого (<c>Job.Error</c>), наружу
/// не выходила.
/// </summary>
public record JobDto(
    Guid Id,
    string Kind,
    Guid TargetId,
    string Status,
    string Title,
    string? Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt = null,
    string? Error = null);

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

    /// <summary>
    /// Задача по id — В ЛЮБОМ статусе, включая завершившуюся. <c>null</c>: нет такой или чужая.
    ///
    /// Заведено под ось ACT (issue #898). До неё «мои активные» хватало: у экрана есть колокольчик,
    /// и человек узнаёт итог уведомлением. У того, кто запустил задачу снаружи, колокольчика нет —
    /// задача уходит из списка активных, и по этому исчезновению успех неотличим от отказа.
    ///
    /// Завершённые задачи не удаляются, так что спросить можно и спустя сутки.
    /// </summary>
    Task<JobDto?> GetAsync(Guid jobId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Есть ли у пользователя активная (Queued/Running) задача по данной цели — защита от дубля.
    ///
    /// <paramref name="kinds" /> сужает вопрос до перечисленных видов; пусто — любой вид. Сужение
    /// нужно там, где цель у разных операций ОДНА: комплект — цель и сборки, и отправки почтой, и
    /// сверки качества. Без него запущенная сверка (минуты) блокировала бы сборку — с сообщением,
    /// называющим сборку, которой не существует (issue #898).
    /// </summary>
    Task<bool> HasActiveForTargetAsync(Guid userId, Guid targetId, CancellationToken ct, params JobKind[] kinds);

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
