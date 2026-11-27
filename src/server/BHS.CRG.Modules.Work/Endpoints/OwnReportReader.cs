using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Охват двух GET своих отчётов. Штатно определяется связью учётной записи с сотрудником.</summary>
internal interface IOwnReportReader
{
    Task<ReportViewer> ViewerAsync(CancellationToken ct);
}

internal sealed class OwnReportReader(IModuleUser user, IModuleEmployees employees) : IOwnReportReader
{
    public async Task<ReportViewer> ViewerAsync(CancellationToken ct) =>
        ReportViewer.Own(await ReportEndpoints.MeAsync(user, employees, ct));
}
