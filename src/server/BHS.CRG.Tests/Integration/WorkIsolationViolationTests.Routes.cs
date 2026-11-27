using System.Net;
using System.Text.Json;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private const HttpStatusCode Ok = HttpStatusCode.OK;
    private const HttpStatusCode Closed = HttpStatusCode.Forbidden;
    private const HttpStatusCode Missing = HttpStatusCode.NotFound;

    private enum ControlReader { Executive, Owner, Manager }
    private enum ExpandedReader { Installer, Unassigned }
    private enum WorkProfile { Installer, Unassigned, Executive, Owner, Manager }
    private enum PhotoKind { Original, Preview }

    private sealed record Probe(string Template, HttpStatusCode Installer, HttpStatusCode Unassigned,
        HttpStatusCode Executive, string? Query = null, string? Signal = null,
        ControlReader Control = ControlReader.Executive, PhotoKind? Photo = null, string? NonReportReason = null,
        ExpandedReader? Expanded = null);

    internal static void CheckMissingProbeControl()
    {
        var probe = new Probe("/api/work/test-only-report", Ok, Ok, Ok);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => CheckProbeControl(probe));
        Assert.Contains(probe.Template, failure.Message, StringComparison.Ordinal);
    }

    private IEnumerable<string> RegisterWorkRoutes(ProbeCatalog catalog, IsolationReaders readers,
        (byte[] Original, byte[] Preview) storedPhoto, bool expanded)
    {
        const string own = "/api/work/reports";
        const string entry = "/api/work/entry/reports";
        const string photo = "/api/work/photos/{photoId:guid}";
        var date = $"date={Day}";
        var site = $"constructionId={world.SiteB}";
        var span = $"from={Day}&to={Day}";
        Probe[] probes =
        [
            new(own, Ok, Closed, Closed, Signal: "items[].id, note", Control: ControlReader.Owner, Expanded: ExpandedReader.Installer),
            new(own + "/{id:guid}", Missing, Closed, Closed, Signal: "id, note", Control: ControlReader.Owner, Expanded: ExpandedReader.Installer),
            new(own + "/{id:guid}/journal", Missing, Missing, Ok, Signal: "items[].changes[note].after", Expanded: ExpandedReader.Installer),
            new(entry + "/{id:guid}", Closed, Missing, Closed, Signal: "id, note", Control: ControlReader.Manager,
                Expanded: ExpandedReader.Unassigned),
            new(entry + "/candidates", Closed, Ok, Closed, date, NonReportReason: "Список сотрудников для ввода.",
                Expanded: ExpandedReader.Unassigned),
            new(entry + "/candidates/{employeeId:guid}/sites", Closed, Ok, Closed, date,
                NonReportReason: "Стройки, доступные для ввода за сотрудника.", Expanded: ExpandedReader.Unassigned),
            new("/api/work/review/queue", Closed, Ok, Ok, $"{site}&{span}", "items[].id, hours", Expanded: ExpandedReader.Unassigned),
            new("/api/work/review/reports/{id:guid}", Closed, Missing, Ok, Signal: "report.id, note", Expanded: ExpandedReader.Unassigned),
            new("/api/work/summary", Closed, Missing, Ok, $"construction={world.SiteB}&{span}", "rows[].cells[].reportId, hours",
                Expanded: ExpandedReader.Unassigned),
            new("/api/work/labor/estimate-vs-actual", Closed, Missing, Ok, $"{site}&{span}",
                NonReportReason: "Статус трудозатрат без подключённого Планирования.", Expanded: ExpandedReader.Unassigned),
            new("/api/work/labor/productivity", Closed, Missing, Ok, $"{site}&{span}&byEmployee=true",
                NonReportReason: "Статус выработки без подключённого Планирования.", Expanded: ExpandedReader.Unassigned),
            new("/api/work/crew-groups", Closed, Ok, Ok, $"{site}&{span}", "items[].id, lines[].reportId, volume",
                Expanded: ExpandedReader.Unassigned),
            new("/api/work/crew-groups/{id:guid}", Closed, Missing, Ok, Signal: "id, lines[].reportId, volume", Expanded: ExpandedReader.Unassigned),
            new(photo, Missing, Missing, Ok, Photo: PhotoKind.Original, Expanded: ExpandedReader.Installer),
            new(photo + "/preview", Missing, Missing, Ok, Photo: PhotoKind.Preview, Expanded: ExpandedReader.Installer),
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
                NonReportReason: "Кандидаты в бригаду для ввода за сотрудника.", Expanded: ExpandedReader.Unassigned),
            new("/api/work/entry/sites/{siteId:guid}/catalog/{kind}", Missing, Missing, Closed, $"reportId={reportB}",
                NonReportReason: "Запись справочника после проверки отчёта.", Expanded: ExpandedReader.Unassigned),
            new("/api/work/materials/hint", Missing, Missing, Closed, $"{site}&{date}",
                NonReportReason: "Материалы справочника, отпуска и сметы в допущенной стройке."),
            new("/api/work/line-details-schema", Ok, Ok, Closed, NonReportReason: "Схема полей строки."),
            new("/api/work/devices/employees/{employeeId:guid}", Closed, Closed, Closed,
                NonReportReason: "Отдельное право на управление устройствами; в ответе сведения об устройствах и отзыве."),
        ];
        var live = EndpointInventory.Routes(testHost.Services).Where(route => EndpointInventory.Matches(EndpointInventory.Route(route), "/api/work")
            && route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true).ToArray();
        Assert.NotEmpty(live);
        var byTemplate = probes.ToDictionary(probe => probe.Template);
        var liveTemplates = live.Select(EndpointInventory.Route).ToHashSet(StringComparer.Ordinal);
        Assert.True(liveTemplates.SetEquals(byTemplate.Keys),
            $"Не описаны: {string.Join(", ", liveTemplates.Except(byTemplate.Keys))}. Лишние: {string.Join(", ", byTemplate.Keys.Except(liveTemplates))}.");
        Assert.All(probes, CheckProbeControl);

        foreach (var route in live)
        {
            var probe = byTemplate[EndpointInventory.Route(route)];
            var path = Bind(route, probe.Query);
            var expectedPhoto = probe.Photo switch
            {
                PhotoKind.Original => storedPhoto.Original,
                PhotoKind.Preview => storedPhoto.Preview,
                _ => null,
            };
            var observation = probe.Signal ?? probe.NonReportReason ?? $"байты {probe.Photo}";
            var installerExpanded = expanded && probe.Expanded == ExpandedReader.Installer;
            var unassignedExpanded = expanded && probe.Expanded == ExpandedReader.Unassigned;
            Add(WorkProfile.Installer, () => CheckWorkProbeAsync(readers.Installer, path, probe,
                installerExpanded ? Ok : probe.Installer, installerExpanded, installerExpanded ? expectedPhoto : null, includesA: true));
            Add(WorkProfile.Unassigned, () => CheckWorkProbeAsync(readers.Unassigned, path, probe,
                unassignedExpanded ? Ok : probe.Unassigned, unassignedExpanded));
            Add(WorkProfile.Executive, () => CheckWorkProbeAsync(readers.Executive, path, probe,
                probe.Executive, probe.Control == ControlReader.Executive, expectedPhoto));
            if (ChecksOwner(probe))
                Add(WorkProfile.Owner, () => CheckWorkProbeAsync(readers.Owner, path, probe, Ok, true, expectedPhoto));
            if (ChecksManager(probe))
                Add(WorkProfile.Manager, () => CheckWorkProbeAsync(readers.Manager, path, probe, Ok, true));

            void Add(WorkProfile profile, Func<Task> verify) =>
                catalog.Add(WorkProbeName(probe.Template, profile), observation, verify);
        }

        var summary = WorkProbeName("/api/work/summary без фильтра стройки", WorkProfile.Unassigned);
        catalog.Add(summary, "reason, boundaryOutcome, total", () => CheckResponseAsync(readers.Unassigned,
            $"/api/work/summary?{span}", Ok, inspect: body =>
            {
                if (expanded)
                {
                    Assert.Null(body.GetProperty("reason").GetString());
                    Assert.Equal("all", body.GetProperty("boundaryOutcome").GetString());
                    Assert.Equal(2, body.GetProperty("total").GetInt32());
                }
                else
                {
                    Assert.Equal("not-assigned", body.GetProperty("reason").GetString());
                    Assert.Equal("not-assigned", body.GetProperty("boundaryOutcome").GetString());
                    Assert.Empty(body.GetProperty("rows").EnumerateArray());
                }
            }, canReadReport: expanded));
        var materials = WorkProbeName("/api/work/materials/hint", WorkProfile.Manager);
        catalog.Add(materials, "reason", () => CheckResponseAsync(readers.Manager, $"/api/work/materials/hint?{site}&{date}",
            Ok, "not-connected"));
        // Обязательные пары берём из инвентаря и матрицы отдельно от регистрации вызовов.
        return [.. liveTemplates.SelectMany(template => RequiredWorkProbeNames(byTemplate[template])), summary, materials];
    }

    private static IEnumerable<string> RequiredWorkProbeNames(Probe probe)
    {
        yield return WorkProbeName(probe.Template, WorkProfile.Installer);
        yield return WorkProbeName(probe.Template, WorkProfile.Unassigned);
        yield return WorkProbeName(probe.Template, WorkProfile.Executive);
        if (ChecksOwner(probe)) yield return WorkProbeName(probe.Template, WorkProfile.Owner);
        if (ChecksManager(probe)) yield return WorkProbeName(probe.Template, WorkProfile.Manager);
    }

    private static string WorkProbeName(string template, WorkProfile profile) => $"GET {template} [{profile}]";

    private static bool ChecksOwner(Probe probe) => probe.Photo is not null || probe.Control == ControlReader.Owner;

    private static bool ChecksManager(Probe probe) => probe.Control == ControlReader.Manager
        || probe.Template == "/api/work/entry/sites/{siteId:guid}/catalog/{kind}";

    private Task CheckWorkProbeAsync(HttpClient client, string path, Probe probe, HttpStatusCode status, bool positive,
        byte[]? expectedPhoto = null, bool includesA = false)
    {
        var inspect = status == Ok && expectedPhoto is null
            ? (Action<JsonElement>)(body => CheckWorkPayload(body, probe.Template, positive, includesA))
            : null;
        return CheckResponseAsync(client, path, status, canReadReport: positive && probe.NonReportReason is null,
            expectedPhoto: expectedPhoto, inspect: inspect);
    }

    private void CheckWorkPayload(JsonElement body, string template, bool positive, bool includesA)
    {
        switch (template)
        {
            case "/api/work/reports":
                CheckOwnFeed(body, includesA, includesB: positive);
                break;
            case "/api/work/reports/{id:guid}":
            case "/api/work/entry/reports/{id:guid}":
                CheckReport(body, reportB, lineB, HoursB, VolumeB, marker);
                Assert.Equal(marker, body.GetProperty("note").GetString());
                break;
            case "/api/work/review/reports/{id:guid}":
                var report = body.GetProperty("report");
                CheckReport(report, reportB, lineB, HoursB, VolumeB, marker);
                Assert.Equal(marker, report.GetProperty("note").GetString());
                break;
            case "/api/work/reports/{id:guid}/journal":
                AssertJournalNote(body, marker);
                break;
            case "/api/work/review/queue":
                Assert.Equal(positive ? 1 : 0, body.GetProperty("total").GetInt32());
                if (positive)
                {
                    var row = Assert.Single(Items(body));
                    Assert.Equal(reportB, row.GetProperty("id").GetGuid());
                    Assert.Equal(HoursB, row.GetProperty("hours").GetDecimal());
                }
                else
                {
                    Assert.Empty(Items(body));
                    Assert.Equal("not-assigned", body.GetProperty("reason").GetString());
                }
                break;
            case "/api/work/summary":
                Assert.Equal(1, body.GetProperty("total").GetInt32());
                var summary = Assert.Single(body.GetProperty("rows").EnumerateArray());
                Assert.Equal(employeeB, summary.GetProperty("employeeId").GetGuid());
                var entry = Assert.Single(Assert.Single(summary.GetProperty("cells").EnumerateArray()).EnumerateArray());
                Assert.Equal(reportB, entry.GetProperty("reportId").GetGuid());
                Assert.Equal(HoursB, entry.GetProperty("hours").GetDecimal());
                break;
            case "/api/work/crew-groups":
                if (positive) CheckCrewGroup(Assert.Single(Items(body)));
                else Assert.Empty(Items(body));
                break;
            case "/api/work/crew-groups/{id:guid}":
                CheckCrewGroup(body);
                break;
            default:
                CheckWorkReference(body, template, positive, includesA);
                break;
        }
    }

    private static void AssertJournalNote(JsonElement body, string expected)
    {
        var changes = Items(body).SelectMany(item => item.GetProperty("changes").EnumerateArray());
        var note = Assert.Single(changes, change => change.GetProperty("field").GetString() == "note"
            && change.GetProperty("line").ValueKind == JsonValueKind.Null);
        Assert.Equal(expected, note.GetProperty("after").GetString());
    }

    internal static void CheckJournalNoteFromAnotherField()
    {
        const string expected = "Work25-journal-marker";
        using var body = JsonDocument.Parse("""
            {"items":[{"changes":[
                {"line":null,"field":"note","before":"Work25-journal-marker","after":"другое примечание"},
                {"line":"fixture-line","field":"location","after":"Work25-journal-marker"}
            ]}]}
            """);
        Assert.Throws<Xunit.Sdk.EqualException>(() => AssertJournalNote(body.RootElement, expected));
    }

    private void CheckCrewGroup(JsonElement group)
    {
        Assert.Equal(groupB, group.GetProperty("id").GetGuid());
        var line = Assert.Single(group.GetProperty("lines").EnumerateArray());
        Assert.Equal(reportB, line.GetProperty("reportId").GetGuid());
        Assert.Equal(VolumeB, line.GetProperty("volume").GetDecimal());
    }

    private void CheckWorkReference(JsonElement body, string template, bool positive, bool includesA)
    {
        switch (template)
        {
            case "/api/work/labor/estimate-vs-actual":
            case "/api/work/labor/productivity":
                Assert.Equal("not-connected", body.GetProperty("state").GetString());
                break;
            case "/api/work/entry/reports/candidates":
            case "/api/work/entry/reports/candidates/{employeeId:guid}/sites":
            case "/api/work/entry/sites/{id:guid}/crew-candidates":
                if (positive)
                    Assert.Contains(Items(body), item => item.GetProperty(template.EndsWith("/candidates", StringComparison.Ordinal)
                        ? "employeeId" : "id").GetGuid() ==
                        (template.EndsWith("/sites", StringComparison.Ordinal) ? world.SiteB : employeeB));
                else Assert.Empty(Items(body));
                break;
            case "/api/work/constructions/{id:guid}/crew-candidates":
                Assert.Empty(Items(body));
                Assert.Equal("not-assigned", body.GetProperty("reason").GetString());
                break;
            case "/api/work/entry/sites/{siteId:guid}/catalog/{kind}":
                Assert.Contains(Items(body), item => item.GetProperty("id").GetGuid() == world.WorkType);
                break;
            case "/api/work/sites/mine":
                if (includesA) Assert.Equal(world.SiteA, Assert.Single(Items(body)).GetProperty("id").GetGuid());
                else Assert.Empty(Items(body));
                break;
            case "/api/work/photos/limits":
                Assert.True(body.GetProperty("count").GetInt32() > 0);
                Assert.True(body.GetProperty("maxBytes").GetInt64() > 0);
                break;
            case "/api/work/line-details-schema":
                Assert.Contains(body.GetProperty("fields").EnumerateArray(), field => field.GetProperty("key").GetString() == "work25");
                break;
            case "/api/work/assignments":
                Assert.Equal(world.SiteB, body.GetProperty("construction").GetProperty("id").GetGuid());
                Assert.Equal(new[] { employeeB, managerEmployee }.Order(), Items(body).Select(item => item.GetProperty("employeeId").GetGuid()).Order());
                Assert.All(Items(body), item => Assert.False(item.GetProperty("canChange").GetBoolean()));
                break;
            case "/api/work/assignments/candidates":
                var assigned = Assert.Single(Items(body), item => item.GetProperty("employeeId").GetGuid() == employeeB);
                Assert.False(assigned.GetProperty("eligible").GetBoolean());
                break;
            default:
                throw new InvalidOperationException($"{template}: не описан справочный ответ.");
        }
    }

    private static JsonElement.ArrayEnumerator Items(JsonElement body) => body.GetProperty("items").EnumerateArray();

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
        bool canReadReport = false, byte[]? expectedPhoto = null, Action<JsonElement>? inspect = null)
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
        if (inspect is not null)
        {
            using var body = JsonDocument.Parse(content);
            inspect(body.RootElement);
        }
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
