using System.Security.Claims;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Generation;

public static class GenerationEndpoints
{
    public static void MapGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/generate").RequireAuthorization();

        g.MapPost("/{instanceId:guid}", async (
            Guid instanceId, GenerateRequest req, IMediator m, ClaimsPrincipal user) =>
        {
            var format = req.Format.ToLower() == "docx" ? OutputFormat.Docx : OutputFormat.Pdf;
            var generatedBy = user.FindFirst("displayName")?.Value;
            var file = await m.Send(new GenerateDocumentCommand(instanceId, format, generatedBy));
            return Results.Ok(new { file.Id, file.BlobPath, Format = file.Format.ToString() });
        });

        g.MapGet("/download/{instanceId:guid}/{format}", async (
            Guid instanceId, string format, IMediator m, IBlobStorage blob) =>
        {
            var outputFormat = format.ToLower() == "docx" ? OutputFormat.Docx : OutputFormat.Pdf;
            var inst = await m.Send(new GetDocumentInstanceQuery(instanceId));
            if (inst is null) return Results.NotFound();

            var generatedFile = inst.GeneratedFiles.FirstOrDefault(f => f.Format == outputFormat);
            if (generatedFile is null) return Results.NotFound();

            var stream = await blob.DownloadAsync(generatedFile.BlobPath);
            var contentType = outputFormat == OutputFormat.Pdf
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            var ext = outputFormat == OutputFormat.Pdf ? "pdf" : "docx";

            return Results.File(stream, contentType, $"document.{ext}");
        });

        // Список плагинов
        g.MapGet("/plugins", (BHS.CRG.Infrastructure.Plugins.IPluginHost host)
            => Results.Ok(host.Plugins.Select(p => new { p.Id, p.DisplayName, p.ProvidedSchemas })));

        g.MapPost("/plugins/{pluginId}/search", async (
            string pluginId, PluginSearchRequest req,
            BHS.CRG.Infrastructure.Plugins.IPluginHost host) =>
        {
            var plugin = host.GetById(pluginId);
            if (plugin is null) return Results.NotFound();
            var result = await plugin.SearchAsync(req.EntityType, req.Query);
            return Results.Ok(result);
        });

        g.MapPost("/plugins/{pluginId}/fetch", async (
            string pluginId, PluginFetchRequest req,
            BHS.CRG.Infrastructure.Plugins.IPluginHost host) =>
        {
            var plugin = host.GetById(pluginId);
            if (plugin is null) return Results.NotFound();
            var data = await plugin.FetchAsync(req.EntityType, req.ExternalId);
            return Results.Ok(data);
        });
    }

    record GenerateRequest(string Format);
    record PluginSearchRequest(string EntityType, string Query);
    record PluginFetchRequest(string EntityType, string ExternalId);
}
