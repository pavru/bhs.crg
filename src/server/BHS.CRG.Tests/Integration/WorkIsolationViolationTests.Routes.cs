using System.Net;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Tests.Integration;

public sealed partial class WorkIsolationViolationTests
{
    private const HttpStatusCode Ok = HttpStatusCode.OK;
    private const HttpStatusCode Closed = HttpStatusCode.Forbidden;
    private const HttpStatusCode Missing = HttpStatusCode.NotFound;

    private enum ControlReader { Executive, Owner, Manager }
    private enum PhotoKind { Original, Preview }

    private sealed record Probe(string Template, HttpStatusCode Installer, HttpStatusCode Unassigned,
        HttpStatusCode Executive, string? Query = null, string? Signal = null,
        ControlReader Control = ControlReader.Executive, PhotoKind? Photo = null, string? NonReportReason = null);

    [Fact]
    public void Маршрут_без_положительной_проверки_диагностируется()
    {
        var probe = new Probe("/api/work/test-only-report", Ok, Ok, Ok);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => CheckProbeControl(probe));
        Assert.Contains(probe.Template, failure.Message, StringComparison.Ordinal);
    }

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
            new(own, Ok, Closed, Closed, Signal: marker, Control: ControlReader.Owner),
            new(own + "/{id:guid}", Missing, Closed, Closed, Signal: marker, Control: ControlReader.Owner),
            new(own + "/{id:guid}/journal", Missing, Missing, Ok, Signal: marker),
            new(entry + "/{id:guid}", Closed, Missing, Closed, Signal: marker, Control: ControlReader.Manager),
            new(entry + "/candidates", Closed, Ok, Closed, date, NonReportReason: "Список сотрудников для ввода."),
            new(entry + "/candidates/{employeeId:guid}/sites", Closed, Ok, Closed, date,
                NonReportReason: "Стройки, доступные для ввода за сотрудника."),
            new("/api/work/review/queue", Closed, Ok, Ok, $"{site}&{span}", reportKey),
            new("/api/work/review/reports/{id:guid}", Closed, Missing, Ok, Signal: marker),
            new("/api/work/summary", Closed, Missing, Ok, $"construction={world.SiteB}&{span}", reportKey),
            new("/api/work/labor/estimate-vs-actual", Closed, Missing, Ok, $"{site}&{span}",
                NonReportReason: "Итоги трудозатрат по стройке."),
            new("/api/work/labor/productivity", Closed, Missing, Ok, $"{site}&{span}&byEmployee=true",
                NonReportReason: "Проценты к сметной норме в разрешённом охвате."),
            new("/api/work/crew-groups", Closed, Ok, Ok, $"{site}&{span}", reportKey),
            new("/api/work/crew-groups/{id:guid}", Closed, Missing, Ok, Signal: reportKey),
            new(photo, Missing, Missing, Ok, Photo: PhotoKind.Original),
            new(photo + "/preview", Missing, Missing, Ok, Photo: PhotoKind.Preview),
            new("/api/work/photos/limits", Ok, Ok, Closed, NonReportReason: "Настройки загрузки фото."),
            new("/api/work/sites/mine", Ok, Ok, Ok, date, NonReportReason: "Стройки по действующим назначениям."),
            // Справочники и назначения читают свои записи. Совпадение стройки или сотрудника с
            // отчётом само по себе ничего не говорит о доступе к содержимому отчёта.
            new("/api/work/assignments", Closed, Ok, Closed, site, NonReportReason: "Назначения на стройку."),
            new("/api/work/assignments/candidates", Closed, Ok, Closed, $"{site}&kind=installer&dateFrom={Day}&dateTo={Day}",
                NonReportReason: "Кандидаты на назначение."),
            new("/api/work/constructions/{id:guid}/crew-candidates", Ok, Ok, Ok, date,
                NonReportReason: "Кандидаты в бригаду."),
            new("/api/work/entry/sites/{id:guid}/crew-candidates", Closed, Ok, Closed, date,
                NonReportReason: "Кандидаты в бригаду для ввода за сотрудника."),
            new("/api/work/entry/sites/{siteId:guid}/catalog/{kind}", Missing, Missing, Closed, $"reportId={reportB}",
                NonReportReason: "Справочник для ввода, доступ к которому проверяется по отчёту."),
            new("/api/work/materials/hint", Missing, Missing, Closed, $"{site}&{date}",
                NonReportReason: "Материалы справочника, отпуска и сметы в допущенной стройке."),
            new("/api/work/line-details-schema", Ok, Ok, Closed, NonReportReason: "Схема полей строки."),
        ];
        var live = EndpointInventory.Routes(testHost.Services).Where(route => EndpointInventory.Matches(EndpointInventory.Route(route), "/api/work")
            && route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true).ToArray();
        Assert.NotEmpty(live);
        var byTemplate = probes.ToDictionary(probe => probe.Template);
        var liveTemplates = live.Select(EndpointInventory.Route).ToHashSet(StringComparer.Ordinal);
        Assert.True(liveTemplates.SetEquals(byTemplate.Keys),
            $"Не описаны: {string.Join(", ", liveTemplates.Except(byTemplate.Keys))}. Лишние: {string.Join(", ", byTemplate.Keys.Except(liveTemplates))}.");
        Assert.All(probes, CheckProbeControl);

        var storedPhoto = await ReadStoredPhotoAsync();
        foreach (var route in live)
        {
            var probe = byTemplate[EndpointInventory.Route(route)];
            var path = Bind(route, probe.Query);
            await CheckResponseAsync(installer, path, probe.Installer, probe.Template == own ? reportA.ToString() : null);
            await CheckResponseAsync(unassigned, path, probe.Unassigned);
            var executiveSignal = probe.Control == ControlReader.Executive ? probe.Signal : null;
            var expectedPhoto = probe.Photo switch
            {
                PhotoKind.Original => storedPhoto.Original,
                PhotoKind.Preview => storedPhoto.Preview,
                _ => null,
            };
            await CheckResponseAsync(executive, path, probe.Executive, executiveSignal,
                canReadReport: executiveSignal is not null, expectedPhoto: expectedPhoto);
            if (expectedPhoto is not null)
                await CheckResponseAsync(owner, path, Ok, expectedPhoto: expectedPhoto);
            if (probe.Control != ControlReader.Executive)
            {
                var reader = probe.Control == ControlReader.Owner ? owner : manager;
                Assert.NotNull(probe.Signal);
                await CheckResponseAsync(reader, path, Ok, probe.Signal, canReadReport: true);
            }
        }

        await CheckResponseAsync(unassigned, $"/api/work/summary?{span}", Ok, "not-assigned");
        await CheckResponseAsync(manager, $"/api/work/entry/sites/{world.SiteB}/catalog/work-types?reportId={reportB}",
            Ok, world.WorkType.ToString());
        await CheckResponseAsync(manager, $"/api/work/materials/hint?{site}&{date}", Ok, "not-connected");
    }

    private static void CheckProbeControl(Probe probe)
    {
        var text = !string.IsNullOrWhiteSpace(probe.Signal);
        var photo = probe.Photo is not null;
        var nonReport = !string.IsNullOrWhiteSpace(probe.NonReportReason);
        Assert.True((text ? 1 : 0) + (photo ? 1 : 0) + (nonReport ? 1 : 0) == 1,
            $"{probe.Template}: нужен контроль текста, фото или обоснование отсутствия содержимого отчёта.");
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
        bool canReadReport = false, byte[]? expectedPhoto = null)
    {
        using var response = await client.GetAsync(path);
        var expectPhoto = expectedPhoto is not null;
        var isPhoto = response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true;
        Assert.True(isPhoto == expectPhoto, $"{path}: ожидалось фото = {expectPhoto}, получено = {isPhoto}.");
        if (expectedPhoto is not null)
        {
            Assert.True(response.StatusCode == status, $"{path}: ожидалось фото, получен ответ {(int)response.StatusCode}.");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.AsSpan().SequenceEqual(expectedPhoto), $"{path}: фото отличается от сохранённого файла.");
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
        Assert.False(content.Contains(photoB.ToString(), StringComparison.OrdinalIgnoreCase)
            || content.Contains(retainedPhotoB.ToString(), StringComparison.OrdinalIgnoreCase), $"{path}: в ответе есть фото отчёта Б.");
    }
}
