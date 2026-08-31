using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Jobs;
using MediatR;
using System.Text.Json;

namespace BHS.CRG.Infrastructure.Jobs;

/// <summary>
/// Единственное место, где долгая операция ставится в работу. Порядок проверок здесь — не стиль, а
/// содержание: см. <see cref="IOperationLauncher" />.
/// </summary>
public class OperationLauncher(
    IJobService jobs,
    IDataSetService dataSets,
    IRecognitionPreflight preflight,
    IMediator mediator) : IOperationLauncher
{
    public async Task<Guid?> AssembleDocumentSetAsync(
        Guid setId, Guid userId, IReadOnlyList<Guid>? instanceIds, CancellationToken ct)
    {
        var set = await mediator.Send(new GetDocumentSetQuery(setId), ct);
        if (set is null) return null;

        // Защиты от дубля у сборки не было вовсе: экран прикрыт блокировкой кнопки, но блокировка
        // живёт во вкладке — перезагрузка её снимает, и вторая сборка пишет тот же выход комплекта
        // поверх первой. Ставим здесь, а не в адаптере, чтобы правило было одно на все входы.
        //
        // Вид задачи назван ЯВНО: комплект — цель ещё и для отправки почтой, и для сверки качества.
        // Без сужения идущая сверка (минуты) отвергала бы сборку — сообщением про сборку, которой
        // не существует.
        if (await jobs.HasActiveForTargetAsync(userId, setId, ct, JobKind.AssembleDocumentSet))
            throw new ConflictException("Сборка этого комплекта уже идёт.");

        var payload = instanceIds is { Count: > 0 } ids
            ? JsonSerializer.Serialize(new { instanceIds = ids })
            : null;
        return await jobs.EnqueueAsync(
            JobKind.AssembleDocumentSet, userId, setId, $"Сборка комплекта «{set.Name}»", payload, ct);
    }

    public async Task<RecognitionLaunch?> RecognizeFileAsync(
        Guid fileId, Guid userId, bool confirm, CancellationToken ct)
    {
        // План первым: он и проверяет существование набора, и решает судьбу вызова. Пока предполёт
        // стоял раньше, неверный идентификатор получал ответ «распознавать некому» — отказ, который
        // отправляет разбираться с настройками движка вместо опечатки в адресе.
        var plan = await dataSets.PlanFileRecognitionAsync(fileId, confirm, ct);
        if (plan is null) return null;
        if (!plan.Background)
        {
            if (await preflight.CheckAsync(ct) is { } shortBlock)
                return new RecognitionLaunch(null, null, shortBlock);
            await dataSets.RecognizeFileAsync(fileId, confirm, ct);
            return new RecognitionLaunch(null, null, null);
        }

        return await EnqueueRecognitionAsync(plan, userId, ct);
    }

    public async Task<RecognitionLaunch?> RecognizeSourceAsync(
        Guid sourceId, Guid userId, bool confirm, CancellationToken ct)
    {
        var plan = await dataSets.PlanRecognitionAsync(sourceId, confirm, ct);
        if (plan is null) return null;
        if (!plan.Background)
        {
            if (await preflight.CheckAsync(ct) is { } shortBlock)
                return new RecognitionLaunch(null, null, shortBlock);
            var source = await dataSets.RecognizePdfSourceAsync(sourceId, confirm, ct);
            return source is null ? null : new RecognitionLaunch(null, source, null);
        }

        return await EnqueueRecognitionAsync(plan, userId, ct);
    }

    /// <summary>
    /// Постановка распознавания в фон — общая для обоих входов, потому что и работа общая: ГОСТ-
    /// профиль распознаёт НАБОР целиком, даже когда попросили один его источник (группировка живёт
    /// на наборе, источников распознавание не создаёт).
    ///
    /// Отсюда цель задачи — <see cref="RecognizePlan.FileId" />, а не то, что назвал вызывающий.
    /// Вход по источнику ставил задачу с его идентификатором, а исполнитель ищет по цели
    /// <c>DataSetFile</c>: 202 с номером задачи приходил честно, и задача падала с «DataSetFile …
    /// not found» — отказ, который вызывающий видел уже не в ответе на запуск.
    /// </summary>
    private async Task<RecognitionLaunch> EnqueueRecognitionAsync(
        RecognizePlan plan, Guid userId, CancellationToken ct)
    {
        // Дубль — по НАБОРУ и только по видам распознавания: сборка комплекта или снятие копии с
        // тем же идентификатором цели к делу не относятся.
        if (await jobs.HasActiveForTargetAsync(userId, plan.FileId, ct,
                JobKind.RecognizeGostSet, JobKind.RecognizeDocument, JobKind.RecognizeTable))
            throw new ConflictException("По этому набору уже идёт распознавание.");

        // Предполёт ПОСЛЕ проверки «уже идёт»: он может уйти к движку на полторы минуты (холодная
        // модель), и всё это время окно для второй такой же задачи оставалось бы открытым.
        if (await preflight.CheckAsync(ct) is { } blocked) return new RecognitionLaunch(null, null, blocked);

        return new RecognitionLaunch(
            await jobs.EnqueueAsync(JobKind.RecognizeGostSet, userId, plan.FileId, plan.Title, null, ct),
            null, null);
    }
}
