using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правила назначения (ТЗ WORK-21, WORK-27, WORK-46, CORE-34.1; change <c>work-11-assignments</c>): пересечения
/// считает сервер и называет пересекающееся; сотрудник годен на весь период; пачка — всё или ничего; кандидат
/// «годен» ровно тогда, когда заведение его не отвергнет; назначение держит сотрудника.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class AssignmentRulesTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly AssignmentWorld world = new(host);
    private Guid _type;
    private Guid _siteA;
    private Guid _siteB;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await world.ResetAsync();
        _type = await world.DirectoryAsync();
        _siteA = await world.SiteAsync("Стройка А");
        _siteB = await world.SiteAsync("Стройка Б");
        // «На любых стройках» — здесь проверяются правила, а не ворота.
        (_, _client) = await world.UserAsync("Администратор", WorkModule.Assign, WorkModule.SitesAll);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> PostAsync(Guid employee, Guid site, string kind, string from, string? to) =>
        _client.PostAsJsonAsync("/api/work/assignments", Body(employee, site, kind, from, to));

    [Fact]
    public async Task Пересечение_на_той_же_стройке_409_с_периодом_две_стройки_в_один_день_успех()
    {
        var installer = await world.EmployeeAsync(_type, "Лебедев Артём Ильич");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(installer, _siteA, "installer", "2026-11-01", "2026-11-15")).StatusCode);

        var overlap = await PostAsync(installer, _siteA, "installer", "2026-11-10", "2026-11-20");
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Contains("01.11.2026–15.11.2026", await ErrorAsync(overlap));

        // Повтор того же назначения — тот же отказ: заведение версию не называет, двойное заведение ловит пересечение.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(installer, _siteA, "installer", "2026-11-01", "2026-11-15")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(installer, _siteB, "installer", "2026-11-10", "2026-11-10")).StatusCode);
        Assert.Equal(1, await world.CountAsync(_siteA));
    }

    [Theory]
    [InlineData(null, "2026-11-01", "уволен с 01.11.2026")]
    [InlineData("2026-10-28", null, "принят с 28.10.2026")]
    public async Task Уволенный_или_не_принятый_на_часть_периода_422_с_причиной(string? hired, string? dismissed, string reason)
    {
        var employee = await world.EmployeeAsync(_type, "Фролов Денис Олегович", hired, dismissed);

        var response = await PostAsync(employee, _siteA, "installer", "2026-10-25", "2026-11-05");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(reason, (await JsonAsync(response)).GetProperty("reason").GetString());
        Assert.Equal(0, await world.CountAsync(_siteA));
    }

    [Fact]
    public async Task Архивный_422_менеджер_без_учётной_записи_422()
    {
        var archived = await world.EmployeeAsync(_type, "Белова Ольга Петровна");
        await world.ArchiveAsync(archived);
        var unlinked = await world.EmployeeAsync(_type, "Никитин Глеб Романович");

        var gone = await PostAsync(archived, _siteA, "installer", "2026-11-01", "2026-11-15");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gone.StatusCode);
        Assert.Equal("в архиве", (await JsonAsync(gone)).GetProperty("reason").GetString());

        var manager = await PostAsync(unlinked, _siteA, "manager", "2026-11-01", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, manager.StatusCode);
        Assert.Equal("сотрудник не связан с учётной записью", (await JsonAsync(manager)).GetProperty("reason").GetString());
    }

    /// <summary>Пачка из пяти с одним уволенным — 422 называет его, и не заведено ни одного (ТЗ WORK-21).</summary>
    [Fact]
    public async Task Пачка_с_одним_уволенным_422_ни_одного_назначения()
    {
        var crew = new List<Guid>();
        foreach (var name in new[] { "Гусев Антон", "Зайцев Пётр", "Морозов Иван", "Павлов Егор" })
            crew.Add(await world.EmployeeAsync(_type, name));
        crew.Add(await world.EmployeeAsync(_type, "Тихонов Лев", dismissed: "2026-11-15"));

        var response = await _client.PostAsJsonAsync("/api/work/assignments/bulk", new
        {
            constructionId = _siteA, kind = "installer", dateFrom = "2026-11-01", dateTo = "2026-11-30", employeeIds = crew,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var refused = Assert.Single((await JsonAsync(response)).GetProperty("refused").EnumerateArray());
        Assert.Equal("Тихонов Лев", refused.GetProperty("name").GetString());
        Assert.Equal("уволен с 15.11.2026", refused.GetProperty("reason").GetString());
        Assert.Equal(0, await world.CountAsync(_siteA));

        var good = await _client.PostAsJsonAsync("/api/work/assignments/bulk", new
        {
            constructionId = _siteA, kind = "installer", dateFrom = "2026-11-01", dateTo = "2026-11-30", employeeIds = crew[..4],
        });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(4, await world.CountAsync(_siteA));
    }

    [Fact]
    public async Task Кандидаты_с_причинами_исключения()
    {
        var fit = await world.EmployeeAsync(_type, "Андреев Кирилл");
        var gone = await world.EmployeeAsync(_type, "Борисов Марк", dismissed: "2026-11-15");
        var busy = await world.EmployeeAsync(_type, "Васильев Олег");
        await world.AssignedAsync(busy, _siteA, AssignmentKind.Installer, "2026-11-10", "2026-11-20");

        var response = await _client.GetAsync(
            $"/api/work/assignments/candidates?constructionId={_siteA}&kind=installer&dateFrom=2026-11-01&dateTo=2026-11-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await JsonAsync(response)).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("employeeId").GetGuid());
        Assert.True(items[fit].GetProperty("eligible").GetBoolean());
        Assert.False(items[gone].GetProperty("eligible").GetBoolean());
        Assert.Equal("уволен с 15.11.2026", items[gone].GetProperty("reason").GetString());
        Assert.False(items[busy].GetProperty("eligible").GetBoolean());
        Assert.Equal("уже назначен 10.11.2026–20.11.2026", items[busy].GetProperty("reason").GetString());
        Assert.Equal("2026-11-10", items[busy].GetProperty("assigned").GetProperty("dateFrom").GetString());
    }

    /// <summary>Назначение держит сотрудника: удалить нельзя, отказ называет «назначения — 3» (ТЗ CORE-34.1).</summary>
    [Fact]
    public async Task Удаление_сотрудника_с_тремя_назначениями_отказ_с_числом()
    {
        var employee = await world.EmployeeAsync(_type, "Жуков Семён Ильич");
        await world.AssignedAsync(employee, _siteA, AssignmentKind.Installer, "2026-09-01", "2026-09-30");
        await world.AssignedAsync(employee, _siteA, AssignmentKind.Installer, "2026-10-01", "2026-10-31");
        await world.AssignedAsync(employee, _siteB, AssignmentKind.Installer, "2026-11-01", "2026-11-30");
        var (_, admin) = await world.UserAsync("Кадры", CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);
        var version = (await JsonAsync(await admin.GetAsync($"/api/employees/{employee}"))).GetProperty("version").GetString();

        var refusal = await admin.SendAsync(Versioned(HttpMethod.Delete, $"/api/employees/{employee}", version));

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains("назначения — 3", await ErrorAsync(refusal));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/employees/{employee}")).StatusCode);
    }

    /// <summary>
    /// Справочника сотрудников нет — адреса, читающие сотрудников, отвечают 409 <c>directory-missing</c>, а не
    /// пустым списком (ТЗ CORE-20.4; work-10 отложил перевод сюда).
    /// </summary>
    [Fact]
    public async Task Справочник_не_заведён_409_а_не_пусто()
    {
        await world.ResetAsync();
        var site = await world.SiteAsync("Стройка без справочника");
        var (_, client) = await world.UserAsync("Администратор", WorkModule.Assign, WorkModule.SitesAll);

        foreach (var response in new[]
                 {
                     await client.GetAsync($"/api/work/assignments/candidates?constructionId={site}&kind=installer&dateFrom=2026-11-01&dateTo=2026-11-30"),
                     await client.PostAsJsonAsync("/api/work/assignments", Body(Guid.NewGuid(), site, "installer", "2026-11-01", "2026-11-15")),
                     await client.GetAsync($"/api/work/assignments?constructionId={site}"),
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal("directory-missing", body.GetProperty("code").GetString());
            Assert.Contains("«Сотрудник» не заведён", body.GetProperty("error").GetString());
        }
    }
}
