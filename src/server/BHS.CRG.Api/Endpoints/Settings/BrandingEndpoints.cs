using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Api.Endpoints.Common;
using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Application.Branding;
using Microsoft.Net.Http.Headers;

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
        // Предел частоты — как у прочих адресов, открытых до входа: этот отдаёт файл до пяти
        // мегабайт кому угодно (ревью PR #1061). Он щедрый нарочно: за общим адресом офиса сидят
        // десятки людей, а оформление читает КАЖДЫЙ экран — предел, годный для «забыли пароль»,
        // оставил бы контору без шапки и логотипа.
        var pub = app.MapGroup("/api/branding").RequireRateLimiting("branding");
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

            // Метка версии отдаётся ЧЕРЕЗ Results.File, а не заголовком вручную: только так
            // конвейер сравнивает её с If-None-Match и отвечает 304. Заголовок, поставленный
            // руками, выглядел как работающий ETag — и не работал (ревью PR #1061).
            //
            // Без fileDownloadName: имя файла в Content-Disposition превращает ответ во вложение, а
            // это картинка в <img> на странице входа.
            return Results.File(logo.Content, logo.MimeType, enableRangeProcessing: false,
                entityTag: EntityTagHeaderValue.Parse(logo.ETag));
        });

        MapIcons(app);

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

    /// <summary>
    /// Значки вкладки и приложения и манифест (issue #1315) — анонимные, как всё оформление: на них
    /// ссылается <c>index.html</c> любой страницы, включая страницу входа.
    ///
    /// <para><b>Своя политика частоты, а не общая с названием и логотипом.</b> Значок и манифест
    /// браузер запрашивает сверх названия и логотипа — манифест при каждой загрузке страницы; в
    /// общей корзине они съедали бы её заметно быстрее, и 429 пришёл бы адресу с названием — то
    /// есть экрану без шапки.</para>
    ///
    /// <para><b>Отказа у значка нет.</b> Нет логотипа или значки из него не построены — отдаётся
    /// стандартная картинка: значок стоит на странице входа, и ошибка здесь — пустая вкладка. О том,
    /// что значки не построены, говорит <c>GET /api/branding</c> (<c>icons.state</c>), а экран
    /// настройки — словами.</para>
    /// </summary>
    private static void MapIcons(IEndpointRouteBuilder app)
    {
        var icons = app.MapGroup("/api/branding").RequireRateLimiting("branding-icons");

        icons.MapGet("/icons/{name}", async (
            string name, string? v, IBrandingService branding, HttpContext http, CancellationToken ct) =>
        {
            if (!BrandIconKinds.ByRoute.TryGetValue(name, out var kind)) return Results.NotFound();
            var icon = await branding.GetIconAsync(kind, ct);

            // index.html — статический файл и метку версии знать не может: его ссылка идёт без ?v,
            // и такой ответ обязан проверяться на свежесть (304 по тегу). Длинный кеш — только
            // адресу с ДЕЙСТВУЮЩЕЙ меткой: так значок зовут манифест и само приложение. Чужая или
            // устаревшая метка длинного кеша не получает — иначе он закрепил бы за старым адресом
            // новую картинку, а за новым — старую.
            // ⚠️ Заголовки решают не всё: Chromium значок ВКЛАДКИ по HTTP не перепроверяет вовсе —
            // держит его в своей базе по адресу значка до недели. Для вкладки свежесть даёт только
            // смена адреса, то есть метка; ставит её приложение (см. index.html).
            http.Response.Headers.CacheControl = v == icon.Version ? "public, max-age=604800" : "no-cache";
            return Results.File(icon.Content, icon.MimeType, enableRangeProcessing: false,
                entityTag: EntityTagHeaderValue.Parse(icon.ETag));
        });

        icons.MapGet("/manifest.webmanifest", async (
            IBrandingService branding, HttpContext http, CancellationToken ct) =>
        {
            var content = Encoding.UTF8.GetBytes(BrandManifest.Build(await branding.GetAsync(ct)));
            http.Response.Headers.CacheControl = "no-cache";
            var tag = Convert.ToHexStringLower(SHA256.HashData(content))[..16];
            return Results.File(content, BrandManifest.ContentType, enableRangeProcessing: false,
                entityTag: new EntityTagHeaderValue($"\"{tag}\""));
        });
    }

    private record ProductNameRequest(string? ProductName);
}
