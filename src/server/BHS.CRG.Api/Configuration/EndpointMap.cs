using BHS.CRG.Api.Endpoints.Account;
using BHS.CRG.Api.Endpoints.Attachments;
using BHS.CRG.Api.Endpoints.Auth;
using BHS.CRG.Api.Endpoints.Backup;
using BHS.CRG.Api.Endpoints.Maintenance;
using BHS.CRG.Api.Endpoints.Support;
using BHS.CRG.Api.Endpoints.Catalog;
using BHS.CRG.Api.Endpoints.Recognition;
using BHS.CRG.Api.Endpoints.DataSets;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Api.Endpoints.Email;
using BHS.CRG.Api.Endpoints.Subscriptions;
using BHS.CRG.Api.Endpoints.Generation;
using BHS.CRG.Api.Endpoints.Reconciliation;
using BHS.CRG.Api.Endpoints.Resolution;
using BHS.CRG.Api.Endpoints.Templates;
using BHS.CRG.Api.Endpoints.Settings;
using BHS.CRG.Api.Endpoints.Users;
using BHS.CRG.Api.Endpoints.Activity;
using BHS.CRG.Api.Endpoints.Schema;
using BHS.CRG.Api.Endpoints.Notifications;
using BHS.CRG.Api.Endpoints.Jobs;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Карта эндпоинтов (вынесено из <c>Program.cs</c>, issue #1030). Порядок перенесён дословно:
/// модули (<c>MapAppModules</c>) регистрируются там же, где и стояли.
/// </summary>
internal static class EndpointMap
{
    /// <summary>Все маршруты приложения одним вызовом.</summary>
    internal static void MapAppEndpoints(this WebApplication app)
    {
    app.MapAttachmentEndpoints();
    app.MapAuthEndpoints();
    app.MapAccountEndpoints();
    app.MapUserEndpoints();
    app.MapRoleEndpoints();
    app.MapActivityEndpoints();
    app.MapBackupEndpoints();
    app.MapBugReportEndpoints();
    app.MapMaintenanceEndpoints();
    app.MapCatalogEndpoints();
    app.MapPrimitiveTypeEndpoints();
    app.MapEnumTypeEndpoints();
    app.MapRecognitionProfileEndpoints();

    // MCP-эндпоинт (issue #415) — под той же аутентификацией, что и REST: агент действует ОТ ИМЕНИ
    // пользователя своим JWT, поэтому отдельной модели доступа не заводим.
    app.MapMcp("/mcp").RequireAuthorization();
    app.MapDocumentTypeEndpoints();
    app.MapCommonDataEndpoints();
    app.MapTemplateEndpoints();
    app.MapTemplateAssetEndpoints();
    app.MapTypstUserLibEndpoints();
    app.MapDocumentSetEndpoints();
    app.MapGenerationEndpoints();

    // Адреса включённых модулей — каждый в своей группе (см. AppModuleExtensions.MapAppModules).
    app.MapAppModules();
    app.MapDataSetEndpoints();
    app.MapDataSetBindingEndpoints();
    app.MapDataSetBindingTemplateEndpoints();
    app.MapReconciliationEndpoints();
    app.MapObservationEndpoints();
    app.MapObjectResolveEndpoints();
    app.MapSettingsEndpoints();
    app.MapUpdateEndpoints();
    app.MapEmailEndpoints();
    app.MapSubscriptionEndpoints();

    // Версия приложения (для отображения в UI и трассировки сборок). Анонимно — виден и на странице входа.
    // Сам номер версии анонимен намеренно, а git-хеш и дата сборки — нет: репозиторий публичный, и по
    // хешу сборка сопоставляется с историей до конкретного коммита. Вошедшему пользователю они видны.
    app.MapGet("/api/version", (HttpContext ctx) =>
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly()!;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var parts = info.Split('+', 2); // "0.1.0+<sha>"
        if (ctx.User.Identity?.IsAuthenticated != true)
            return Results.Ok(new { version = parts[0], commit = "", buildDate = (DateTimeOffset?)null });

        var commit = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : ""; // короткий sha для показа
        DateTimeOffset? buildDate = null;
        try { buildDate = File.GetLastWriteTimeUtc(asm.Location); } catch { /* single-file/unknown */ }
        return Results.Ok(new { version = parts[0], commit, buildDate });
    }).AllowAnonymous();
    app.MapNotificationsEndpoints();
    app.MapJobsEndpoints();
    app.MapTagsEndpoints();
    }
}
