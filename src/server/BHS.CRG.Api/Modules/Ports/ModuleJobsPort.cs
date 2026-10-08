using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Фоновые задачи модуля — в общую очередь (ТЗ CORE-3).
///
/// <para>Вид задачи один на все модули (<see cref="JobKind.ModuleWork" />), а операция лежит в
/// аргументах: перечисление видов принадлежит ядру, и строка на каждую операцию модуля означала бы
/// правку ядра под каждый модуль.</para>
///
/// <para>⚠️ Постановка операции без обработчика — ОТКАЗ ЗДЕСЬ, а не тихая запись в очередь. Задача
/// без исполнителя дошла бы до фонового цикла и упала там — то есть отказ увидел бы не тот, кто
/// ошибся, и не тогда, когда ошибся. Проверка возможна именно здесь: обработчики регистрируют
/// модули, и в этой области служб они все налицо.</para>
/// </summary>
public sealed class ModuleJobsPort(
    IJobService jobs, IActivityActor actor, ModuleRegistry modules, IEnumerable<IModuleJobHandler> handlers)
    : IModuleJobs
{
    public Task<Guid> EnqueueAsync(string operation, Guid targetId, string title, string? payload = null,
        CancellationToken ct = default)
    {
        ModuleWork.EnsureNamedByModule(operation, modules);
        ModuleWork.EnsureHandled(operation, handlers);

        // Владельца не спрашивают у вызывающего — его берут из запроса, как автора записи журнала.
        // Вне запроса владельца нет вовсе (Guid.Empty): так же поставлена плановая резервная копия, и
        // отказ такой задачи уходит общесистемным уведомлением, а не в чей-то личный колокольчик.
        var owner = actor.Current.Id ?? Guid.Empty;

        return jobs.EnqueueAsync(JobKind.ModuleWork, owner, targetId, title,
            ModuleWork.Wrap(operation, payload), ct);
    }

    /// <summary>
    /// Состояние задачи — БЕЗ проверки владельца, в отличие от личного списка задач человека.
    ///
    /// ⚠️ Первая редакция спрашивала задачу правами текущего пользователя, и это была дыра в обещании
    /// порта (ревью PR #1106): у задачи, поставленной вне запроса, владельца нет вовсе, и модуль,
    /// показывающий ход по сохранённому идентификатору, навсегда получал «нет такой задачи» — вместо
    /// идущей или упавшей. То же с задачей, поставленной другим человеком: счёт открывают вдвоём.
    /// Читаются этим путём ТОЛЬКО работы модулей — ход операций ядра модулю не виден (см.
    /// <see cref="IJobService.GetModuleWorkAsync" />).
    /// </summary>
    public async Task<ModuleJobState?> GetAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobs.GetModuleWorkAsync(jobId, ct);
        return job is null ? null : State(job);
    }

    public async Task<IReadOnlyDictionary<Guid, ModuleJobState>> GetManyAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default) =>
        (await jobs.GetModuleWorksAsync(jobIds, ct)).ToDictionary(j => j.Id, State);

    private static ModuleJobState State(JobDto job)
    {
        if (!Enum.TryParse<ModuleJobStatus>(job.Status, out var status))
            throw new InvalidOperationException(
                $"Статус задачи «{job.Status}» не выражен в контрактах модулей. Зеркало ModuleJobStatus " +
                "разошлось с JobStatus — его сверяет ModulePortMirrorTests.");

        return new ModuleJobState(job.Id, job.Title, status, job.Progress, job.Error);
    }
}
