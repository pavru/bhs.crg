using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Jobs;

/// <summary>Вид фоновой задачи. Расширяется по мере перевода долгих операций в фон.</summary>
public enum JobKind
{
    RecognizeGostSet = 0,
    RecognizeTable = 1,
    /// <summary>Точечное перераспознавание одного документа набора (не всего альбома).</summary>
    RecognizeDocument = 2,
    /// <summary>Сборка всего комплекта в один PDF (генерация недостающих + склейка по порядку).</summary>
    AssembleDocumentSet = 3,
    /// <summary>Отправка собранного комплекта подписчикам по email (вложение/ссылка).</summary>
    SendEmail = 4,
}

/// <summary>Статус фоновой задачи. Активные (для индикатора) — Queued/Running.</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>Отменена пользователем ДО старта. Отмена возможна только из очереди (Queued) —
    /// уже выполняемые (Running) добегают до конца.</summary>
    Cancelled = 4,
}

/// <summary>
/// Фоновая задача — источник истины для индикатора активных операций и для переживания reload/много-
/// вкладочности. Долгие операции (распознавание набора/таблицы, минуты) ставятся в очередь и выполняются
/// hosted-сервисом вне HTTP-реквеста (эндпоинт возвращает 202+Id сразу). Итог операции по-прежнему
/// уходит в подсистему уведомлений (разные жизненные циклы: Job меняется и завершается — Notification
/// неизменяем и копится). См. консультацию Архитектора «индикатор активных long-running задач».
/// </summary>
public class Job : Entity
{
    public JobKind Kind { get; private set; }
    public JobStatus Status { get; private set; }

    /// <summary>Кто запустил (владелец) — по нему индикатор показывает «мои» задачи.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Цель операции — обобщённая (DataSetSource и т.п.), чтобы точечные операции (P6)
    /// ложились той же задачей с другой целью, без переделки job-системы.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>Доп. аргументы задачи (JSON) — напр. {"firstPageIndex":3} для распознавания таблицы.</summary>
    public string? Payload { get; private set; }

    /// <summary>Отображаемый заголовок для индикатора («Распознавание листов», «Распознавание таблицы»).</summary>
    public string Title { get; private set; } = "";

    /// <summary>Честный текстовый прогресс без выдуманных процентов («12 из 57 листов»). Null — ещё нет.</summary>
    public string? Progress { get; private set; }

    public string? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    private Job() { }

    public static Job Create(JobKind kind, Guid userId, Guid targetId, string title, string? payload = null)
        => new()
        {
            Kind = kind,
            Status = JobStatus.Queued,
            UserId = userId,
            TargetId = targetId,
            Title = title,
            Payload = payload,
        };

    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;

    public void Start()
    {
        Status = JobStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }

    public void ReportProgress(string progress)
    {
        Progress = progress;
        TouchUpdatedAt();
    }

    public void Succeed()
    {
        Status = JobStatus.Succeeded;
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }

    public void Fail(string error)
    {
        Status = JobStatus.Failed;
        Error = error;
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }

    /// <summary>Отменить задачу — ТОЛЬКО пока она в очереди (Queued). Выполняемая (Running) добегает до
    /// конца (отмена на середине дорогого vision-прогона не поддерживается по решению пользователя).
    /// Возвращает false, если отменить нельзя (уже стартовала/завершена).</summary>
    public bool TryCancel()
    {
        if (Status != JobStatus.Queued) return false;
        Status = JobStatus.Cancelled;
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
        return true;
    }

    /// <summary>Пометить брошенной при рестарте (in-process очередь потеряна) — вызывается на старте
    /// для зависших Queued/Running, чтобы индикатор не «висел» вечно.</summary>
    public void MarkAbandoned()
    {
        Status = JobStatus.Failed;
        Error = "Задача прервана перезапуском сервера — запустите операцию заново.";
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }
}
