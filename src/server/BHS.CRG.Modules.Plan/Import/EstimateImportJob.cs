using System.Runtime.ExceptionServices;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Фоновая операция «разобрать файл импорта» (change <c>plan-07-estimate-import</c>, ТЗ PLAN-7, STG-12): смета
/// на тысячи строк с проверкой по XSD в запрос не помещается.
///
/// <para>Исход записывается у импорта всегда: ЛСР — <c>parsed</c> с моделью разбора и не разобранным; ВОР и
/// КА — <c>parsed</c> с одним видом документа (редакцией они не становятся, их прикладывают к смете —
/// <c>plan-11</c>); файл, который не разобран вовсе, — <c>rejected</c> с причиной словами разбора
/// (<c>plan-05</c>): неизвестная версия называет версию.</para>
///
/// <para>⚠️ <b>Отменённый импорт — не отказ</b>: импорт отменяют и пока файл разбирается, и пока задача ждёт
/// очереди. Разобранное тогда выбрасывается, а задача завершается без ошибки — красная задача «разбирать
/// нечего» говорила бы о сбое там, где человек сам передумал.</para>
/// </summary>
public sealed class EstimateImportJob(
    PlanDbContext db, [FromKeyedServices(PlanModule.ModuleCode)] IModuleBlobs blobs, ILogger<EstimateImportJob> logger)
    : IModuleJobHandler
{
    public const string OperationCode = "plan.import.parse";

    /// <summary>Сколько раз исход перезаписывается, проиграв одновременной записи номера задачи.</summary>
    private const int SaveAttempts = 3;

    public string Operation => OperationCode;

    public async Task RunAsync(ModuleJobRun run, CancellationToken ct)
    {
        var import = await db.EstimateImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == run.TargetId, ct);
        if (import is null)
        {
            logger.LogInformation("Импорт {ImportId} отменён до разбора — разбирать нечего.", run.TargetId);
            return;
        }
        if (import.State != EstimateImportState.Parsing)
            throw new ConflictException("Разбор этого импорта уже завершён другой попыткой.");

        Action<EstimateImport> outcome;
        Exception? failure = null;
        GrandSmetaFile? file = null;
        try
        {
            await using var content = await blobs.OpenAsync(import.BlobPath, ct);
            file = GrandSmetaFile.Open(content);
            outcome = Outcome(file);
        }
        catch (GrandSmetaRefusedException refused)
        {
            var source = file?.Source;
            outcome = i =>
            {
                if (source is not null) i.Opened(source);
                else i.Recognised(refused.Kind, refused.ProgramVersion);
                i.Reject(refused.Reason.ToString(), refused.Message, DateTimeOffset.UtcNow);
            };
            failure = refused;
        }
        catch (Exception crash) when (crash is not OperationCanceledException)
        {
            // Не наш отказ — сбой хранилища или чужое исключение разбора. Исход всё равно записывается: иначе
            // импорт «разбирался» бы вечно. Чужой текст человеку не отдаём — он в журнале сервера.
            logger.LogError(crash, "Файл импорта {ImportId} не разобран: сбой.", run.TargetId);
            outcome = i => i.Reject(EstimateImport.FailedCode,
                "Файл не удалось разобрать: внутренняя ошибка. Отмените импорт и загрузите файл ещё раз; если " +
                "повторится — подробности в журнале сервера.", DateTimeOffset.UtcNow);
            failure = crash;
        }

        if (!await SaveAsync(run, outcome)) return;
        // Отказ — задача упала с текстом разбора: колокольчик скажет о нём тому, кто загрузил файл.
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Разбор по виду документа. Читатель ВОР и КА тоже проходит: разобранной ведомостью назвать можно
    /// только ту, что читается.</summary>
    private static Action<EstimateImport> Outcome(GrandSmetaFile file)
    {
        var source = file.Source;
        switch (file.Kind)
        {
            case GrandSmetaKind.LocalEstimate:
                var model = LocalEstimateReader.Read(file);
                var (parsed, unparsed) = EstimateImportJson.Write(model);
                return i =>
                {
                    i.Opened(source);
                    i.ParsedLocalEstimate(model.Requisites.Number, model.Requisites.Name, parsed, unparsed, DateTimeOffset.UtcNow);
                };
            case GrandSmetaKind.VolumeStatement:
                VolumeStatementReader.Read(file);
                break;
            default:
                ConjunctureReader.Read(file);
                break;
        }
        return i =>
        {
            i.Opened(source);
            i.ParsedOther(DateTimeOffset.UtcNow);
        };
    }

    /// <summary>
    /// Записать исход. Проиграв по версии записи (адрес загрузки дописал номер задачи), исход перечитывает
    /// импорт и ложится заново: разобранное не выбрасывается. <c>false</c> — импорт тем временем отменили.
    ///
    /// <para>Без токена отмены: исход пишется и тогда, когда задачу остановили.</para>
    /// </summary>
    private async Task<bool> SaveAsync(ModuleJobRun run, Action<EstimateImport> outcome)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var import = await db.EstimateImports.FirstOrDefaultAsync(i => i.Id == run.TargetId, CancellationToken.None);
            if (import is null)
            {
                logger.LogInformation("Импорт {ImportId} отменён, пока файл разбирался: разобранное выброшено.", run.TargetId);
                return false;
            }
            if (import.JobId is null) import.Queued(run.JobId);
            outcome(import);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return true;
            }
            catch (ConflictException) when (attempt < SaveAttempts)
            {
            }
        }
    }
}
