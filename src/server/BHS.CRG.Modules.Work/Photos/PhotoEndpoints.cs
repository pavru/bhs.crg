using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Photos;

/// <summary>
/// Адреса фото строки (ТЗ WORK-33, WORK-34, WORK-39, AUTH-8, AUTH-9; change <c>work-17-photos</c>):
/// <c>PUT</c> и <c>DELETE /api/work/reports/{id}/lines/{lineId}/photos/{photoId}</c>, <c>GET /api/work/photos/{photoId}</c>
/// и <c>…/preview</c>.
///
/// <para><b>Файл фото выдают только эти адреса</b> — после чтения отчёта с параметром доступа. Общий адрес файлов ядра
/// файлов модуля не выдаёт (<c>core-attachment-owner</c>); условие безопасности — локальный отчёт аудита, раздел 2.
/// Фото чужого отчёта и фото черновика неавтору отвечают 404, как несуществующее, а не 403.</para>
///
/// <para><b>Ворота.</b> Загрузка и удаление — «любое из прав» <c>work.report.own</c> (свои отчёты) и
/// <c>work.report.review</c> (отчёты своих строек, с <c>work.sites.all</c> — всех); <c>work.report.read</c> правки не
/// даёт — 403. Выдача — ещё и <c>work.report.read</c>, охват тот же, что у журнала отчёта. Адреса стоят отдельно от
/// группы <c>/api/work/reports</c>: та закрыта правом своих отчётов, а политики группы и адреса складываются по «И».</para>
/// </summary>
public static class PhotoEndpoints
{
    public const string LinePath = ReportEndpoints.Path + "/{id:guid}/lines/{lineId:guid}/photos/{photoId:guid}";

    public const string Path = "/api/work/photos/{photoId:guid}";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var write = AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview);
        var read = AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview, WorkModule.ReportRead);
        endpoints.MapPut(LinePath, ReceiveAsync).WithTags("Отчёты").RequireAuthorization(write);
        endpoints.MapDelete(LinePath, RemoveAsync).WithTags("Отчёты").RequireAuthorization(write);
        endpoints.MapGet(Path, OriginalAsync).WithTags("Отчёты").RequireAuthorization(read);
        endpoints.MapGet(Path + "/preview", PreviewAsync).WithTags("Отчёты").RequireAuthorization(read);
    }

    /// <summary>Загрузить файл фото: тело запроса — сам файл, без <c>If-Match</c> (названное исключение).</summary>
    private static Task<IResult> ReceiveAsync(
        Guid id, Guid lineId, Guid photoId, HttpRequest request, IModuleUser user, IModuleReaders readers,
        ManagerSites sites, IModuleEmployees employees, LinePhotos photos, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
            await photos.ReceiveAsync(await WriterAsync(user, readers, sites, employees, ct), id, lineId, photoId,
                request.Body, ct));

    /// <summary>Удалить фото: <c>If-Match</c> с версией отчёта; <c>changeId</c> и <c>deviceTime</c> — в строке запроса.</summary>
    private static Task<IResult> RemoveAsync(
        Guid id, Guid lineId, Guid photoId, Guid? changeId, string? deviceTime, HttpRequest request, IModuleUser user,
        IModuleReaders readers, ManagerSites sites, IModuleEmployees employees, LinePhotos photos, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
        {
            // Версия — первой, до сотрудника и охвата: порядок отказов один у всех пишущих адресов отчёта.
            SeenVersions.Required(request, "отчёта");
            return await photos.RemoveAsync(await WriterAsync(user, readers, sites, employees, ct), id, lineId, photoId,
                changeId, ReportEndpoints.DeviceTimeOf(deviceTime), request, ct);
        });

    private static Task<IResult> OriginalAsync(
        Guid photoId, HttpContext http, IModuleUser user, IModuleReaders readers, ManagerSites sites,
        IModuleEmployees employees, LinePhotos photos, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () => await photos.OpenAsync(
            await ReportJournalEndpoint.ViewerAsync(user, readers, sites, employees, ct), user.Id, photoId, preview: false,
            http, ct));

    private static Task<IResult> PreviewAsync(
        Guid photoId, HttpContext http, IModuleUser user, IModuleReaders readers, ManagerSites sites,
        IModuleEmployees employees, LinePhotos photos, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () => await photos.OpenAsync(
            await ReportJournalEndpoint.ViewerAsync(user, readers, sites, employees, ct), user.Id, photoId, preview: true,
            http, ct));

    /// <summary>
    /// Охват того, кто правит фото: с правом приёмки — стройки менеджера (с <c>work.sites.all</c> — все) и, при праве
    /// своих отчётов, свои; только со своими отчётами — сотрудник учётной записи (без связи — 403 с причиной).
    /// </summary>
    private static async Task<ReportViewer> WriterAsync(
        IModuleUser user, IModuleReaders readers, ManagerSites sites, IModuleEmployees employees, CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        if (!reader.Allows(WorkModule.ReportReview)) return ReportViewer.Own(await ReportEndpoints.MeAsync(user, employees, ct));
        var me = reader.Allows(WorkModule.ReportOwn) && user.Id is { } account
            ? (await employees.OfAccountAsync(account, ct))?.Id
            : null;
        return ReportViewer.Of(await sites.ReachAsync(reader, ct), me);
    }
}
