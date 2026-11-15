using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Мир отчётов (change <c>work-13-report-model</c>): справочник сотрудников, классификатор видов работ, две стройки и
/// вымышленные сотрудники. Подготовка — мимо адресов отчётов, проверяются — адреса.
/// </summary>
internal sealed class ReportWorld(AssignmentHost host)
{
    public const string Day16 = "2026-11-16";

    private readonly AssignmentWorld world = new(host);
    private readonly WorkTypeSeed seed = new(host);

    public AssignmentWorld Assignments => world;

    public Guid EmployeeType { get; private set; }
    public Guid SiteA { get; private set; }
    public Guid SiteB { get; private set; }
    public Guid WorkType { get; private set; }
    public Guid Unit { get; private set; }

    /// <summary>
    /// Чистая база; справочник сотрудников (если <paramref name="directory" />) и классификатор видов работ (если
    /// <paramref name="classifier" />).
    /// </summary>
    public async Task SeedAsync(bool classifier = true, bool directory = true)
    {
        await world.ResetAsync();
        if (directory) EmployeeType = await world.DirectoryAsync();
        SiteA = await world.SiteAsync("Стройка А");
        SiteB = await world.SiteAsync("Стройка Б");
        if (!classifier) return;
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        Unit = unit.Id;
        WorkType = (await seed.RecordAsync(workType.Id, "Прокладка кабеля",
            WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", unit))).Id;
    }

    public Task<Guid> EmployeeAsync(string name) => world.EmployeeAsync(EmployeeType, name);

    /// <summary>Монтажник: сотрудник и связанная с ним учётная запись со своими отчётами.</summary>
    public async Task<(Guid Employee, HttpClient Client)> InstallerAsync(string name)
    {
        var employee = await EmployeeAsync(name);
        var (userId, client) = await world.UserAsync(name, WorkModule.ReportOwn);
        await world.LinkAsync(employee, userId);
        return (employee, client);
    }

    /// <summary>Менеджер стройки: сотрудник с назначением «менеджер» с 01.10.2026 и правом приёмки.</summary>
    public async Task<(Guid Employee, HttpClient Client)> ManagerAsync(string name, Guid site, params string[] extra)
    {
        var employee = await EmployeeAsync(name);
        var (userId, client) = await world.UserAsync(name, [WorkModule.ReportReview, .. extra]);
        await world.LinkAsync(employee, userId);
        await world.AssignedAsync(employee, site, AssignmentKind.Manager, "2026-10-01", null);
        return (employee, client);
    }

    /// <summary>Тело черновика: шапка и (по желанию) одна строка.</summary>
    public object Draft(Guid id, Guid site, decimal? hours = 8m, bool line = true, string date = Day16, Guid? employee = null,
        Guid? changeId = null, string? deviceTime = null, string shift = "day") => new
    {
        id, employeeId = employee, constructionId = site, date, shift, hours, changeId, deviceTime,
        lines = line
            ? new object[] { new { id = Guid.NewGuid(), workTypeId = WorkType, volume = 120m, unitId = Unit, location = "оси 3–4" } }
            : [],
    };

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, string? version, object body) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Put, url, version, body));

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string url, string? version) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete, url, version));

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public async Task<int> ReportCountAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<Report>().CountAsync();
    }

    public async Task<List<ReportChange>> ChangesAsync(Guid reportId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportChange>().AsNoTracking()
            .Where(c => c.ReportId == reportId).OrderBy(c => c.Seq).ToListAsync();
    }

    public async Task<Report?> ReportAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<Report>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
    }
}
