using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

public partial class DataSetPdfRecognitionService
{
    private async Task<RecognizePlan> PlanGostRecognitionAsync(
        DataSetFile file, bool confirm, bool bySource, CancellationToken ct)
    {
        var admission = AdmitGostRecognition(file, confirm, bySource);
        await RefuseTooLongAsync(file.BlobPath, ct);
        return new RecognizePlan(true, "Распознавание листов PDF", file.Id, admission);
    }

    private static GostRecognitionAdmission AdmitGostRecognition(DataSetFile file, bool confirm, bool bySource)
    {
        if (bySource && FindDescriptor(file)?.Kind != PdfProfileKind.Gost)
            throw new InvalidRequestException("Источник не соответствует текущему профилю набора. Запустите распознавание набора.");

        var manual = ParseGrouping(file.Grouping) is { ManuallyEdited: true };
        if (manual && !confirm)
        {
            var target = bySource ? "этого источника" : "набора";
            throw new ManualGroupingConflict(file.Id, file.Name,
                $"Разбиение {target} было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
        }
        return new GostRecognitionAdmission(file.RecognitionRevision, manual && confirm);
    }

    internal async Task RecognizeQueuedGostAsync(
        Guid fileId, GostRecognitionAdmission admission, CancellationToken ct, Func<int, int, Task> onProgress)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null || file.RecognitionRevision != admission.ExpectedRevision ||
            file.Format != DataSetFormat.Pdf || FindDescriptor(file)?.Kind != PdfProfileKind.Gost)
            throw new DataSetRevisionConflict();

        RequireGost();
        AdmitGostRecognition(file, admission.AllowManualOverwrite, bySource: false);
        await RecognizeGostFileAsync(file, ct, onProgress);
    }

    private async Task RequireCurrentRecognitionRevisionAsync(DataSetFile file, CancellationToken ct)
    {
        if (!await db.DataSetFiles.AnyAsync(f => f.Id == file.Id && f.RecognitionRevision == file.RecognitionRevision, ct))
            throw new DataSetRevisionConflict();
    }

    private async Task SaveShortRecognitionAsync(DataSetFile file, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity == file))
        {
            db.ChangeTracker.Clear();
            throw new DataSetRevisionConflict(ex);
        }
    }

    private async Task<byte[]> ReadGostRecognitionPdfAsync(DataSetFile file, CancellationToken ct)
    {
        try
        {
            await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, ct);
            return bytes.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Замена может убрать прежний исходник сразу после принятия задания.
            await RequireCurrentRecognitionRevisionAsync(file, ct);
            throw;
        }
    }
}
