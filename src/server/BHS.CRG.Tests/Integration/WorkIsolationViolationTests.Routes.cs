using System.Net;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Tests.Integration;

public sealed partial class WorkIsolationViolationTests
{
    private const HttpStatusCode Ok = HttpStatusCode.OK;
    private const HttpStatusCode Closed = HttpStatusCode.Forbidden;
    private const HttpStatusCode Missing = HttpStatusCode.NotFound;

    private sealed record Probe(string Template, HttpStatusCode Installer, HttpStatusCode Unassigned,
        HttpStatusCode Executive, string? Query = null, string? ExecutiveSignal = null);

    private async Task CheckWorkRoutesAsync(HttpClient installer, HttpClient owner, HttpClient manager,
        HttpClient unassigned, HttpClient executive)
    {
        const string own = "/api/work/reports";
        const string entry = "/api/work/entry/reports";
        const string photo = "/api/work/photos/{photoId:guid}";
        var date = $"date={Day}";
        var site = $"constructionId={world.SiteB}";
        var span = $"from={Day}&to={Day}";
        var reportKey = reportB.ToString();
        Probe[] probes =
        [
            new(own, Ok, Closed, Closed),
            new(own + "/{id:guid}", Missing, Closed, Closed),
            new(own + "/{id:guid}/journal", Missing, Missing, Ok, ExecutiveSignal: "\"items\":[{"),
            new(entry + "/{id:guid}", Closed, Missing, Closed),
            new(entry + "/candidates", Closed, Ok, Closed, date),
            new(entry + "/candidates/{employeeId:guid}/sites", Closed, Ok, Closed, date),
            new("/api/work/review/queue", Closed, Ok, Ok, $"{site}&{span}", reportKey),
            new("/api/work/review/reports/{id:guid}", Closed, Missing, Ok, ExecutiveSignal: marker),
            new("/api/work/summary", Closed, Missing, Ok, $"construction={world.SiteB}&{span}", reportKey),
            new("/api/work/crew-groups", Closed, Ok, Ok, $"{site}&{span}", reportKey),
            new("/api/work/crew-groups/{id:guid}", Closed, Missing, Ok, ExecutiveSignal: reportKey),
            new(photo, Missing, Missing, Ok),
            new(photo + "/preview", Missing, Missing, Ok),
            new("/api/work/photos/limits", Ok, Ok, Closed),
            new("/api/work/sites/mine", Ok, Ok, Ok, date),
            // Справочники и назначения читают свои записи. Совпадение стройки или сотрудника с
            // отчётом само по себе ничего не говорит о доступе к содержимому отчёта.
            new("/api/work/assignments", Closed, Ok, Closed, site),
            new("/api/work/assignments/candidates", Closed, Ok, Closed, $"{site}&kind=installer&dateFrom={Day}&dateTo={Day}"),
            new("/api/work/constructions/{id:guid}/crew-candidates", Ok, Ok, Ok, date),
            new("/api/work/entry/sites/{id:guid}/crew-candidates", Closed, Ok, Closed, date),
            new("/api/work/entry/sites/{siteId:guid}/catalog/{kind}", Missing, Missing, Closed, $"reportId={reportB}"),
            new("/api/work/line-details-schema", Ok, Ok, Closed),
        ];
        var live = EndpointInventory.Routes(testHost.Services).Where(route => EndpointInventory.Matches(EndpointInventory.Route(route), "/api/work")
            && route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true).ToArray();
        Assert.NotEmpty(live);
        var byTemplate = probes.ToDictionary(probe => probe.Template);
        var liveTemplates = live.Select(EndpointInventory.Route).ToHashSet(StringComparer.Ordinal);
        Assert.True(liveTemplates.SetEquals(byTemplate.Keys),
            $"Не описаны: {string.Join(", ", liveTemplates.Except(byTemplate.Keys))}. Лишние: {string.Join(", ", byTemplate.Keys.Except(liveTemplates))}.");

        foreach (var route in live)
        {
            var probe = byTemplate[EndpointInventory.Route(route)];
            var path = Bind(route, probe.Query);
            await CheckResponseAsync(installer, path, probe.Installer, probe.Template == own ? reportA.ToString() : null);
            await CheckResponseAsync(unassigned, path, probe.Unassigned);
            await CheckResponseAsync(executive, path, probe.Executive, probe.ExecutiveSignal,
                canReadReport: probe.ExecutiveSignal is not null || probe.Template.StartsWith(photo, StringComparison.Ordinal));
        }

        await CheckResponseAsync(owner, own, Ok, marker, canReadReport: true);
        await CheckResponseAsync(owner, $"{own}/{reportB}", Ok, marker, canReadReport: true);
        await CheckResponseAsync(unassigned, $"/api/work/summary?{span}", Ok, "not-assigned");
        await CheckResponseAsync(manager, $"{entry}/{reportB}", Ok, marker, canReadReport: true);
        await CheckResponseAsync(manager, $"/api/work/entry/sites/{world.SiteB}/catalog/work-types?reportId={reportB}",
            Ok, world.WorkType.ToString(), canReadReport: true);
    }

    private string Bind(RouteEndpoint route, string? query)
    {
        var template = EndpointInventory.Route(route);
        var path = template;
        foreach (var parameter in route.RoutePattern.Parameters)
        {
            var value = parameter.Name switch
            {
                "id" when template.StartsWith("/api/work/crew-groups/", StringComparison.Ordinal) => groupB.ToString(),
                "id" when template.StartsWith("/api/work/constructions/", StringComparison.Ordinal)
                      || template.StartsWith("/api/work/entry/sites/", StringComparison.Ordinal) => world.SiteB.ToString(),
                "id" => reportB.ToString(),
                "employeeId" => employeeB.ToString(),
                "photoId" => photoB.ToString(),
                "siteId" => world.SiteB.ToString(),
                "kind" => "work-types",
                _ => throw new InvalidOperationException($"{template}: неизвестный параметр {parameter.Name}."),
            };
            var opening = path.IndexOf("{" + parameter.Name, StringComparison.Ordinal);
            var closing = path.IndexOf('}', opening);
            path = path[..opening] + value + path[(closing + 1)..];
        }
        return query is null ? path : $"{path}?{query}";
    }

    private async Task CheckResponseAsync(HttpClient client, string path, HttpStatusCode status, string? signal = null,
        bool canReadReport = false)
    {
        using var response = await client.GetAsync(path);
        var expectPhoto = canReadReport && status == Ok && path.StartsWith("/api/work/photos/", StringComparison.Ordinal);
        var isPhoto = response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true;
        Assert.True(isPhoto == expectPhoto, $"{path}: ожидалось фото = {expectPhoto}, получено = {isPhoto}.");
        if (expectPhoto)
        {
            Assert.Equal(Ok, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
            return;
        }
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{path}: ожидался {(int)status}, получен {(int)response.StatusCode}. {content}");
        if (signal is not null)
            Assert.True(content.Contains(signal, StringComparison.OrdinalIgnoreCase), $"{path}: ответ не содержит контрольное значение.");
        if (!canReadReport) AssertReportNotDisclosed(content, path);
    }

    private void AssertReportNotDisclosed(string content, string path)
    {
        Assert.False(content.Contains(marker, StringComparison.Ordinal), $"{path}: в ответе есть метка отчёта Б.");
        Assert.False(content.Contains(reportB.ToString(), StringComparison.OrdinalIgnoreCase), $"{path}: в ответе есть отчёт Б.");
        Assert.False(content.Contains(lineB.ToString(), StringComparison.OrdinalIgnoreCase), $"{path}: в ответе есть строка отчёта Б.");
    }
}
