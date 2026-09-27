using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Application.Branding;

namespace BHS.CRG.Api.Endpoints.Settings;

/// <summary>
/// Название продукта и логотип компании (ТЗ CORE-25.1, issue #967).
///
/// <para><b>Чтение — анонимное.</b> Оформление стоит на странице входа, а туда приходят до входа:
/// адрес под правом отдал бы 401 ровно тому экрану, ради которого настройка и заводится. Это
/// осознанная выдача наружу — кто дошёл до страницы входа, тот видит, чей это экземпляр; другого
/// прочтения у «покажите наш логотип на входе» нет. Записан этот выбор в инвентаре адресов
/// (<c>EndpointGateInventoryTests</c>), чтобы он остался решением, а не наблюдением.</para>
///
/// <para><b>Правка — под правом обслуживания экземпляра</b> (<c>core.system.manage</c>), как у
/// прочих настроек экземпляра: это не работа с документами, а настройка стенда.</para>
/// </summary>
public static class BrandingEndpoints
{
    /// <summary>
    /// Что принимаем под логотип. Набор уже, чем у ассетов шаблонов: GIF и анимации в шапке не
    /// нужны, а формат, который не покажет браузер, сделал бы настройку «сохранённой, но пустой».
    /// </summary>
    private static readonly Dictionary<string, string> LogoTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
        };

    public static void MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/branding");
        var admin = app.MapGroup("/api/branding")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        pub.MapGet("/", async (IBrandingService branding, CancellationToken ct) =>
            Results.Ok(await branding.GetAsync(ct)));

        pub.MapGet("/logo", async (IBrandingService branding, HttpContext http, CancellationToken ct) =>
        {
            var logo = await branding.GetLogoAsync(ct);
            if (logo is null) return Results.NotFound();

            // Кеш длинный, но безопасный: адрес зовётся с меткой версии (?v=), а метка меняется при
            // каждой замене файла. Без метки пришлось бы выбирать между «логотип не обновляется» и
            // «картинка качается на каждый экран».
            http.Response.Headers.CacheControl = "public, max-age=604800";
            http.Response.Headers.ETag = logo.ETag;
            return Results.File(logo.Content, logo.MimeType, logo.FileName, enableRangeProcessing: false);
        });

        admin.MapPut("/", async (ProductNameRequest req, IBrandingService branding, CancellationToken ct) =>
        {
            var name = req.ProductName?.Trim();
            if (name is { Length: > BrandingDefaults.MaxProductNameLength })
                return Results.BadRequest(new
                {
                    error = $"Название длиннее {BrandingDefaults.MaxProductNameLength} символов не поместится "
                            + "ни в шапку, ни в заголовок вкладки.",
                });

            // Пусто — снять настройку, вернуться к умолчанию. Сохранять пустую строку нельзя: она
            // означала бы «название есть, и оно никакое» — шапку без имени.
            await branding.SetProductNameAsync(name, ct);
            return Results.Ok(await branding.GetAsync(ct));
        });

        admin.MapPost("/logo", async (IFormFile file, IBrandingService branding, CancellationToken ct) =>
        {
            var ext = Path.GetExtension(file.FileName);
            if (!LogoTypes.TryGetValue(ext, out var mimeType))
                return Results.BadRequest(new
                {
                    error = $"Формат «{ext}» под логотип не годится. Подойдут PNG, JPEG, WebP или SVG.",
                });
            if (UploadLimits.Exceeded(file, UploadLimits.CompanyLogo) is { } tooLarge) return tooLarge;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Results.Ok(await branding.SetLogoAsync(ms.ToArray(), file.FileName, mimeType, ct));
        }).DisableAntiforgery();

        admin.MapDelete("/logo", async (IBrandingService branding, CancellationToken ct) =>
            Results.Ok(await branding.RemoveLogoAsync(ct)));
    }

    private record ProductNameRequest(string? ProductName);
}
