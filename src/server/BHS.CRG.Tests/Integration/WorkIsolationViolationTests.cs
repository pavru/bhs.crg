using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Generation;
using BHS.CRG.Tests.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public class WorkIsolationHost : AssignmentHost
{
    protected override string DatabaseSuffix => "workiso";

    internal WorkIsolationReadControl? ReadControl { get; }

    public WorkIsolationHost() : this(new WorkIsolationReadControl()) { }

    internal WorkIsolationHost(WorkIsolationReadControl control) => ReadControl = control;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (ReadControl is { } control)
            builder.ConfigureTestServices(control.Register);
    }
}

public sealed partial class WorkIsolationViolationTests(WorkIsolationHost host) : IClassFixture<WorkIsolationHost>
{
    private readonly WorkIsolationHost testHost = host;

    [Fact]
    public Task Сегодня_вычисляется_по_поясу_стройки_на_границе_суток() => new WorkIsolationScenario(testHost).CheckLocalDayAsync();

    [Fact]
    public Task Содержимое_отчёта_остаётся_в_схеме_модуля() => new WorkIsolationScenario(testHost).CheckStorageAsync();

    [Fact]
    public Task Сужение_охвата_до_неназначенных_строек_доходит_до_таблиц_и_источников() =>
        new WorkIsolationScenario(testHost).CheckRestrictedReaderAsync();

    [Fact]
    public void Маршрут_без_положительной_проверки_диагностируется() => WorkIsolationScenario.CheckMissingProbeControl();

    [Theory]
    [InlineData("count")]
    [InlineData("skipped")]
    [InlineData("sum")]
    [InlineData("average")]
    [InlineData("min")]
    [InlineData("max")]
    public void Проверка_пустого_итога_отвергает_значение_из_синтетического_ответа(string field) =>
        WorkIsolationScenario.CheckInvalidEmptyTotal(field);
}

internal sealed partial class WorkIsolationScenario(WorkIsolationHost host)
{
    private const string Day = ReportWorld.Day16;
    private const decimal HoursA = 6m, HoursB = 8m, VolumeA = 7m, VolumeB = 100m;
    private readonly WorkIsolationHost testHost = host;
    private readonly ReportWorld world = new(host);
    private readonly string marker = $"Work25-{Guid.NewGuid():N}";
    private readonly Guid reportA = Guid.NewGuid();
    private readonly Guid reportB = Guid.NewGuid();
    private readonly Guid lineA = Guid.NewGuid();
    private readonly Guid lineB = Guid.NewGuid();
    private readonly Guid photoB = Guid.NewGuid();
    private readonly Guid retainedPhotoB = Guid.NewGuid();
    private Guid employeeB;
    private Guid managerEmployee;
    private Guid groupB;
    private Guid installerAccount;
    private Guid unassignedAccount;

    internal async Task CheckLocalDayAsync()
    {
        await world.Assignments.ResetAsync();
        var directory = await world.Assignments.DirectoryAsync();
        var site = await world.Assignments.SiteAsync("Восточная стройка", "Asia/Vladivostok");
        var employee = await world.Assignments.EmployeeAsync(directory, "Монтажник");
        var (account, client) = await world.Assignments.UserAsync("Монтажник", WorkModule.ReportOwn);
        using (client)
        {
            await world.Assignments.LinkAsync(employee, account);
            await world.Assignments.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-01", "2026-11-30");

            // Часы порта в AssignmentHost стоят на 31 октября по Москве и 1 ноября по Владивостоку.
            var access = await client.GetFromJsonAsync<JsonElement>("/api/account/access");
            Assert.Equal("Europe/Moscow", access.GetProperty("company").GetProperty("timeZone").GetString());
            var sites = await client.GetFromJsonAsync<JsonElement>("/api/work/sites/mine");
            var row = Assert.Single(sites.GetProperty("items").EnumerateArray());
            Assert.Equal(site, row.GetProperty("id").GetGuid());
            Assert.Equal("Asia/Vladivostok", row.GetProperty("timeZoneId").GetString());
            Assert.Equal("2026-11-01", row.GetProperty("today").GetString());
        }
    }

    private async Task CheckCombinedRoleAsync()
    {
        var (employee, _, client) = await ReaderAsync("Монтажник и менеджер", WorkModule.ReportOwn, WorkModule.ReportReview);
        using (client)
        {
            await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Manager, "2026-10-01", null);
            var ownOutside = Guid.NewGuid();
            var draft = await CrewWorld.CreateAsync(client, world.Draft(ownOutside, world.SiteB));
            await CrewWorld.SubmitAsync(client, ownOutside, draft);
            var table = await CheckTableAsync(client, WorkTableWorld.Reports, 2, "own-and-sites");
            Assert.Equal(new[] { reportA, ownOutside }.Order(), table.GetProperty("keys").EnumerateArray().Select(value => value.GetGuid()).Order());
            await CheckResponseAsync(client, $"/api/work/review/reports/{ownOutside}", HttpStatusCode.OK, ownOutside.ToString());
            await CheckResponseAsync(client, $"/api/work/summary?from={Day}&to={Day}&construction={world.SiteB}",
                HttpStatusCode.OK, ownOutside.ToString());
            await CheckResponseAsync(client, $"/api/work/review/reports/{reportB}", HttpStatusCode.NotFound);
        }
    }

    internal sealed record IsolationReaders(HttpClient Installer, HttpClient Owner, HttpClient Manager, HttpClient Unassigned,
        HttpClient Executive) : IDisposable
    {
        public void Dispose()
        {
            Installer.Dispose();
            Owner.Dispose();
            Manager.Dispose();
            Unassigned.Dispose();
            Executive.Dispose();
        }
    }

    private async Task<IsolationReaders> SeedAsync()
    {
        testHost.ReportTime = ReviewWorld.Evening16.ToUniversalTime();
        await world.SeedAsync();
        var material = await SeedReportFieldsAsync();
        var (installerId, installerUser, installer) = await ReaderAsync("Монтажник А", WorkModule.ReportOwn);
        installerAccount = installerUser;
        var (ownerId, _, owner) = await ReaderAsync("Монтажник Б", WorkModule.ReportOwn);
        employeeB = ownerId;
        var (assignedManager, manager) = await world.ManagerAsync("Назначенный менеджер", world.SiteB, WorkModule.Assign);
        managerEmployee = assignedManager;
        var (_, unassignedUser, unassigned) = await ReaderAsync("Менеджер без назначений", WorkModule.ReportReview, WorkModule.Assign);
        unassignedAccount = unassignedUser;
        var (_, executive) = await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead, CorePermissions.DataSetsRead);
        await world.Assignments.AssignedAsync(ownerId, world.SiteB, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        await world.Assignments.AssignedAsync(installerId, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        var own = await CrewWorld.CreateAsync(installer, world.Draft(reportA, world.SiteA, hours: HoursA,
            lineId: lineA, location: "Участок А", volume: VolumeA));
        await CrewWorld.SubmitAsync(installer, reportA, own);
        var draft = await CrewWorld.CreateAsync(owner, new
        {
            id = reportB, constructionId = world.SiteB, date = Day, shift = "day", hours = HoursB, note = marker,
            lines = new[]
            {
                new { id = lineB, workTypeId = world.WorkType, unitId = world.Unit, volume = VolumeB, isCrew = true,
                    crewRole = "lead", location = marker, comment = marker, details = new { work25 = marker },
                    materials = new[] { new { nomenclatureId = material, note = marker } },
                    photos = new[] { new { id = photoB, originalName = $"{marker}-1.jpg" },
                        new { id = retainedPhotoB, originalName = $"{marker}-2.jpg" } } },
            },
        });
        await CrewWorld.SubmitAsync(owner, reportB, draft);
        using var upload = await PhotoWorld.UploadAsync(owner, reportB, lineB, photoB, PhotoWorld.Shot());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var retained = await PhotoWorld.UploadAsync(owner, reportB, lineB, retainedPhotoB, PhotoSamples.Jpeg(600, 800));
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        var crew = await executive.GetFromJsonAsync<JsonElement>($"{CrewWorld.Path}?constructionId={world.SiteB}");
        groupB = Assert.Single(crew.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        return new(installer, owner, manager, unassigned, executive);
    }

    private async Task<Guid> SeedReportFieldsAsync()
    {
        using var scope = testHost.Services.CreateScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        var (_, typeEditor) = await world.Assignments.UserAsync("Редактор типов", CorePermissions.TypesRead, CorePermissions.TypesEdit);
        using (typeEditor)
        {
            var types = await typeEditor.GetFromJsonAsync<JsonElement>("/api/document-types");
            var type = Assert.Single(types.EnumerateArray(), item => item.GetProperty("code").GetString() == WorkRecordTypes.LineDetailsCode);
            var id = type.GetProperty("id").GetGuid();
            var details = await typeEditor.GetFromJsonAsync<JsonElement>($"/api/document-types/{id}");
            var schema = JsonNode.Parse(details.GetProperty("schema").GetRawText())!.AsObject();
            schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = "work25", ["type"] = "string", ["title"] = "Сведения посева" });
            using var update = await typeEditor.PutAsJsonAsync($"/api/document-types/{id}/schema", new { schema = schema.ToJsonString() });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        var seed = new WorkTypeSeed(testHost);
        var directory = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
        return (await seed.RecordAsync(directory.Id, "Материал посева")).Id;
    }

    private async Task<(Guid Employee, Guid Account, HttpClient Client)> ReaderAsync(string name, params string[] permissions)
    {
        var employee = await world.EmployeeAsync(name);
        var (user, client) = await world.Assignments.UserAsync(name, [.. permissions, CorePermissions.DataSetsRead]);
        await world.Assignments.LinkAsync(employee, user);
        return (employee, user, client);
    }


    private static async Task<JsonElement> CheckTableAsync(HttpClient client, string path, int count, string boundary)
    {
        using var response = await client.GetAsync(path);
        var json = await ExpectJsonAsync(response, path);
        Assert.Equal(boundary, json.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(count, json.GetProperty("count").GetInt32());
        return json;
    }

    private static void CheckNumericTotal(JsonElement body, string path, string column, params decimal[] values)
    {
        var total = body.GetProperty("totals").GetProperty(column);
        var actual = (Count: total.GetProperty("count").GetInt64(), Skipped: total.GetProperty("skipped").GetInt64(),
            Sum: Read("sum"), Average: Read("average"), Min: Read("min"), Max: Read("max"));
        var expected = values.Length == 0
            ? (0L, 0L, (decimal?)null, (decimal?)null, (decimal?)null, (decimal?)null)
            : ((long)values.Length, 0L, values.Sum(), values.Average(), values.Min(), values.Max());
        Assert.True(actual == expected, $"{path}, колонка «{column}»: ожидался итог {expected}, получен {actual}.");

        decimal? Read(string key)
        {
            var value = total.GetProperty(key);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();
        }
    }

    internal static void CheckInvalidEmptyTotal(string field)
    {
        const string column = "Объём";
        const string path = "/api/work25-test-only";
        var fixture = JsonNode.Parse("""
            {"totals":{"Объём":{"count":0,"skipped":0,"sum":null,"average":null,"min":null,"max":null}}}
            """)!;
        CheckNumericTotal(JsonSerializer.SerializeToElement(fixture), path, column);
        fixture["totals"]![column]![field] = VolumeB;

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            CheckNumericTotal(JsonSerializer.SerializeToElement(fixture), path, column));
        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains(column, failure.Message, StringComparison.Ordinal);
    }


    private static async Task<JsonElement> ExpectJsonAsync(HttpResponseMessage response, string path)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}, {content}");
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }
}
