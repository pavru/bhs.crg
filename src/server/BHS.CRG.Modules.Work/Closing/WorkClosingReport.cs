using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Closing;

public sealed class WorkClosingReport(ReportReads reads, IModuleReaders readers) : IModuleClosingReport
{
    private static readonly ModuleClosingUnit Unit = new("отчёт", "отчёта", "отчётов");

    public string Module => WorkModule.ModuleCode;

    public async Task<ModuleClosingSection> ReportAsync(ModuleClosingScope scope, CancellationToken ct = default)
    {
        var counts = await reads.ClosingCountsAsync(readers.ForSystem("Перечень закрытия периода"), scope, ct);
        var blocking = new List<ModuleClosingLine>();
        if (counts.TryGetValue(ReportStatus.Draft, out var drafts))
            blocking.Add(new("submission-window", "В окне подачи", drafts, Unit,
                Note: "Дождитесь подачи отчётов, включая автоподачу после окончания окна."));
        if (counts.TryGetValue(ReportStatus.Submitted, out var pending))
            blocking.Add(new("pending-review", "Ждут приёмки", pending, Unit));
        if (counts.TryGetValue(ReportStatus.Returned, out var returned))
            blocking.Add(new("returned", "Возвращены на правку", returned, Unit));

        return new("Отчёт относится к периоду по дате смены, сутки — по поясу стройки.", [],
            counts.TryGetValue(ReportStatus.Accepted, out var accepted)
                ? [new("accepted", "Принятые отчёты", accepted, Unit)]
                : [],
            blocking);
    }
}
