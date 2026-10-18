using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Settings;

/// <summary>
/// Профили печати читаемого образа книги (issue #1454, эпик #1452).
///
/// <para><b>Список — правом работы с файлами</b> (<c>core.files.use</c>), а не настройки: меню «Перестроить
/// ▾» у вида счёта нужно бухгалтеру, а не только администратору. Запись — правом обслуживания
/// экземпляра (<c>core.system.manage</c>): профиль меняет раскладку будущих видов у всех.</para>
///
/// <para>Значения перечней — строками (<c>A4</c>, <c>portrait</c>, <c>width</c>), разбор — у
/// <see cref="RenditionLayoutProfileInput.Parse" />. Отказы правки называют поле в <c>details</c>:
/// <c>name</c>, <c>paper</c>, <c>margins.top</c>.</para>
/// </summary>
public static class PrintProfileEndpoints
{
    public record MarginsDto(int Top, int Bottom, int Left, int Right);

    public record PrintProfileRequest(string? Name, string? Paper, string? Orientation, string? Fit, MarginsDto? Margins);

    public record FactoryDto(string Name, string Paper, string Orientation, string Fit, MarginsDto Margins);

    /// <param name="IsFactory">Параметры совпадают с заводскими — «Сбросить к заводским» нечего.</param>
    /// <param name="Factory">Заводские параметры — только у системного: экран показывает, к чему
    /// приведёт «Сбросить к заводским». Сервер — единственный, кто их знает.</param>
    public record PrintProfileView(
        Guid Id, string Name, string Paper, string Orientation, string Fit, MarginsDto Margins, bool IsSystem, bool IsFactory,
        FactoryDto? Factory);

    public static void MapPrintProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/settings/print-profiles")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.FilesUse));
        var write = app.MapGroup("/api/settings/print-profiles")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        read.MapGet("/", async (RenditionLayoutProfiles profiles, CancellationToken ct) =>
            Results.Ok((await profiles.ListAsync(ct)).Select(View)));

        write.MapPost("/", async (PrintProfileRequest req, RenditionLayoutProfiles profiles, CancellationToken ct) =>
            Results.Ok(View(await profiles.CreateAsync(Input(req), ct))));

        write.MapPut("/{id:guid}", async (Guid id, PrintProfileRequest req, RenditionLayoutProfiles profiles,
            CancellationToken ct) => Results.Ok(View(await profiles.UpdateAsync(id, Input(req), ct))));

        write.MapPost("/{id:guid}/reset", async (Guid id, RenditionLayoutProfiles profiles, CancellationToken ct) =>
            Results.Ok(View(await profiles.ResetAsync(id, ct))));

        write.MapDelete("/{id:guid}", async (Guid id, RenditionLayoutProfiles profiles, CancellationToken ct) =>
        {
            await profiles.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static PrintProfileView View(RenditionLayoutProfile p) => new(
        p.Id, p.Name, p.Paper.ToString(), OrientationOf(p), FitOf(p),
        new(p.MarginTop, p.MarginBottom, p.MarginLeft, p.MarginRight), p.IsSystem, p.IsFactory,
        p.IsSystem ? FactoryOf(RenditionLayoutProfile.System()) : null);

    private static FactoryDto FactoryOf(RenditionLayoutProfile p) => new(
        p.Name, p.Paper.ToString(), OrientationOf(p), FitOf(p), new(p.MarginTop, p.MarginBottom, p.MarginLeft, p.MarginRight));

    private static string OrientationOf(RenditionLayoutProfile p) => p.Orientation == PageOrientation.Landscape ? "landscape" : "portrait";

    private static string FitOf(RenditionLayoutProfile p) => p.Fit == PageFit.Width ? "width" : "none";

    private static RenditionLayoutProfileInput Input(PrintProfileRequest req) => RenditionLayoutProfileInput.Parse(
        req.Name, req.Paper, req.Orientation, req.Fit,
        req.Margins is { } m ? new PageMarginsMm(m.Top, m.Bottom, m.Left, m.Right) : null);
}
