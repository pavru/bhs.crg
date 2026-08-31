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
        if (await jobs.HasActiveForTargetAsync(userId, setId, ct))
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
        if (await jobs.HasActiveForTargetAsync(userId, fileId, ct))
            throw new ConflictException("По этому набору уже идёт распознавание.");
        if (await preflight.CheckAsync(ct) is { } blocked) return new RecognitionLaunch(null, null, blocked);

        var plan = await dataSets.PlanFileRecognitionAsync(fileId, confirm, ct);
        if (plan is null) return null;
        if (plan.Background)
            return new RecognitionLaunch(
                await jobs.EnqueueAsync(JobKind.RecognizeGostSet, userId, fileId, plan.Title, null, ct), null, null);

        await dataSets.RecognizeFileAsync(fileId, confirm, ct);
        return new RecognitionLaunch(null, null, null);
    }

    public async Task<RecognitionLaunch?> RecognizeSourceAsync(
        Guid sourceId, Guid userId, bool confirm, CancellationToken ct)
    {
        if (await jobs.HasActiveForTargetAsync(userId, sourceId, ct))
            throw new ConflictException("По этому источнику уже идёт распознавание.");
        if (await preflight.CheckAsync(ct) is { } blocked) return new RecognitionLaunch(null, null, blocked);

        var plan = await dataSets.PlanRecognitionAsync(sourceId, confirm, ct);
        if (plan is null) return null;
        if (plan.Background)
            return new RecognitionLaunch(
                await jobs.EnqueueAsync(JobKind.RecognizeGostSet, userId, sourceId, plan.Title, null, ct), null, null);

        var source = await dataSets.RecognizePdfSourceAsync(sourceId, confirm, ct);
        return source is null ? null : new RecognitionLaunch(null, source, null);
    }
}
