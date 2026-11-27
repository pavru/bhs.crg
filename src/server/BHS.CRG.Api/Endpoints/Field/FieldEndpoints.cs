using BHS.CRG.Application.Branding;
using BHS.CRG.Modules;
using Microsoft.Net.Http.Headers;

namespace BHS.CRG.Api.Endpoints.Field;

internal static class FieldEndpoints
{
    internal static void MapFieldEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<ModuleRegistry>().IsEnabled("work")) return;

        var field = app.MapGroup("/api/field");
        field.MapGet("/info", async (IBrandingService branding, HttpResponse response, CancellationToken ct) =>
        {
            var info = await branding.GetAsync(ct);
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                organizationName = info.ProductName,
                logoUrl = info.HasLogo ? "/api/field/info/logo" : null,
                protocolVersions = new[] { 1 },
                minimumSenderVersionCode = 1,
            });
        }).AllowAnonymous().RequireRateLimiting("field-info");

        field.MapGet("/info/logo", async (IBrandingService branding, HttpResponse response, CancellationToken ct) =>
        {
            var logo = await branding.GetLogoAsync(ct);
            if (logo is null) return Results.NotFound(new { reason = "logo-unavailable" });
            response.Headers.CacheControl = "no-cache";
            return Results.File(logo.Content, logo.MimeType, entityTag: EntityTagHeaderValue.Parse(logo.ETag));
        }).AllowAnonymous().RequireRateLimiting("field-info");

        field.MapGet("/ping", (HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
        FieldSessionEndpoints.Map(field);
    }
}
