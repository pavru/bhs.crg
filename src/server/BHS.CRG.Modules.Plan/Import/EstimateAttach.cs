using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>Отказ приложить файл с не разобранными позициями без согласия — числа полями, как у редакции.</summary>
public sealed class PartialAttachmentException(int parsed, int inFile, int unparsed) : ConflictException(
    $"В файле {unparsed} не разобранных позиций: разобрано {parsed} из {inFile}. Приложение из такого файла " +
    "заводится только с вашего согласия — подтвердите, и в нём будут только разобранные позиции.")
{
    public int Parsed { get; } = parsed;
    public int InFile { get; } = inFile;
    public int Unparsed { get; } = unparsed;
}

/// <summary>Приложение заведено: что это и было ли оно заведено раньше тем же импортом.</summary>
public sealed record EstimateAttached(EstimateAttachment Attachment, bool Repeated);

/// <summary>
/// Приложить разобранный импорт к смете (change <c>plan-11-conjuncture</c>, ТЗ PLAN-9, PLAN-7, решение В-7 (а)):
/// конъюнктурный анализ или ведомость объёмов заводится из модели, которую человек прочитал в отчёте разбора, — файл
/// второй раз не разбирается.
///
/// <para><b>Импорт уходит, файл остаётся</b>: путь в хранилище переходит к приложению одной фиксацией с удалением
/// импорта. Отчёта у приложения нет — оно само и есть разобранное, а «ждёт решения» с ним кончается.</para>
///
/// <para><b>Повтор — не второе приложение.</b> Ключ импорта у приложения единственный: принятие того же импорта ещё
/// раз (импорта уже нет) называет заведённое, одновременное — отказ базы словами «уже заведено».</para>
/// </summary>
public sealed class EstimateAttach(PlanDbContext db, IModuleUser user)
{
    /// <summary>Заведённое раньше этим импортом в этой смете; <c>null</c> — не заводилось.</summary>
    public async Task<EstimateAttachment?> FoundAsync(Guid estimateId, Guid importId, CancellationToken ct) =>
        (EstimateAttachment?)await db.ConjunctureAnalyses.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ImportKey == importId && a.EstimateId == estimateId, ct)
        ?? await db.VolumeStatements.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ImportKey == importId && s.EstimateId == estimateId, ct);

    /// <param name="acceptPartial">Согласие приложить разобранное без не разобранных позиций — как у редакции.</param>
    public async Task<EstimateAttached> RunAsync(Guid estimateId, Guid importId, bool acceptPartial, CancellationToken ct)
    {
        var import = await db.EstimateImports.FirstOrDefaultAsync(i => i.Id == importId, ct);
        if (import is null)
            return await FoundAsync(estimateId, importId, ct) is { } before
                ? new EstimateAttached(before, Repeated: true)
                : throw new NotFoundException("Импорт не найден — возможно, его отменили.");
        if (import.EstimateId is { } target && target != estimateId)
            throw new InvalidRequestException("Импорт загружен в другую смету — прикладывается он к ней.");
        if (import.NotAttachable() is { } refusal) throw new ConflictException(refusal);

        var unparsed = EstimateImportJson.Unparsed(import);
        var parsed = import.ParsedPositions ?? 0;
        if (parsed == 0)
            throw new ConflictException("Из файла не разобрано ни одной позиции — прикладывать нечего.");
        if (unparsed.Count > 0 && !acceptPartial)
            throw new PartialAttachmentException(parsed, import.PositionsInFile ?? parsed + unparsed.Count, unparsed.Count);

        var now = DateTimeOffset.UtcNow;
        EstimateAttachment attachment = import.Kind switch
        {
            GrandSmetaKind.Conjuncture => ConjunctureAnalysis.From(import,
                EstimateImportJson.Conjuncture(import)!, now, user.Id, user.Name),
            _ => VolumeStatement.From(import, EstimateImportJson.VolumeStatement(import)!, now, user.Id, user.Name),
        };
        db.Add(attachment);
        db.EstimateImports.Remove(import);
        await db.SaveChangesAsync(ct);
        return new EstimateAttached(attachment, Repeated: false);
    }
}
