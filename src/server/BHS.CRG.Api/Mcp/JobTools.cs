using System.ComponentModel;
using System.Security.Claims;
using BHS.CRG.Application.Jobs;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>Состояние фоновой задачи. Ровно то, что нужно, чтобы решить: ждать, читать результат или
/// разбираться с отказом.</summary>
/// <param name="Status">Queued (в очереди), Running (выполняется), Succeeded, Failed, Cancelled.</param>
/// <param name="Progress">Честный текстовый прогресс без выдуманных процентов («12 из 57 листов»).</param>
/// <param name="Error">Причина отказа — заполнена только у Failed.</param>
/// <param name="TargetId">Объект операции: комплект у сборки, набор или источник у распознавания.</param>
public record JobInfo(
    Guid JobId, string Kind, string Status, string Title, Guid TargetId,
    string? Progress, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// Наблюдение за фоновой задачей (issue #898).
///
/// Инструмент один, и без него ось ACT неполна: запускать долгие операции агент мог бы, а узнать их
/// итог — нет. У экрана эту роль играет уведомление в колокольчике, у запустившего снаружи
/// уведомлений не бывает, и задача для него просто перестаёт существовать.
/// </summary>
[McpServerToolType]
public class JobTools(IJobService jobs, IHttpContextAccessor http)
{
    /// <summary>Агент действует ОТ ИМЕНИ пользователя — идентичность берём из его же JWT.</summary>
    internal static Guid RequireUserId(IHttpContextAccessor http)
    {
        var user = http.HttpContext?.User;
        var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
        // Пустой идентификатор здесь — не мелочь: задача завелась бы «ничьей», не попала бы ни в чей
        // список активных и оказалась бы недоступна тому, кто её запустил.
        return Guid.TryParse(raw, out var id) && id != Guid.Empty
            ? id
            : throw new McpException("Не удалось определить пользователя: запрос без действительного токена.");
    }

    [McpServerTool(Name = "get_job", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Состояние фоновой задачи")]
    [Description("""
        Чем кончилась (или как идёт) фоновая задача, запущенная assemble_document_set или
        recognize_dataset / recognize_source.

        Спрашивать ОБЯЗАТЕЛЬНО: запуск возвращает только jobId, а сама работа идёт минутами. Пока
        status = Queued или Running, результата ещё нет; Succeeded — операция выполнена; Failed —
        отказ, причина в error; Cancelled — задачу сняли из очереди до старта.

        Опрашивайте с паузой в несколько секунд, а не подряд: сборка комплекта занимает десятки
        секунд, распознавание альбома — минуты. Задачи не удаляются, поэтому спросить можно и много
        позже. Видны только СВОИ задачи; неизвестный jobId — это ошибка, а не «пока нет данных».
        """)]
    public async Task<JobInfo> GetJobAsync(
        [Description("Идентификатор задачи — тот, что вернул запуск операции.")] Guid jobId,
        CancellationToken ct)
    {
        // Отсутствие задачи — ОШИБКА, а не пустой ответ. Пустоту опрашивающий в цикле прочтёт как
        // «ещё не готово» и будет ждать того, чего нет: отказ, переодетый в правдоподобный результат.
        var job = await jobs.GetAsync(jobId, RequireUserId(http), ct)
            ?? throw new McpException(
                "Задача не найдена: такой задачи нет либо она принадлежит другому пользователю.");
        return new JobInfo(job.Id, job.Kind, job.Status, job.Title, job.TargetId,
            job.Progress, job.Error, job.CreatedAt, job.StartedAt, job.FinishedAt);
    }
}
