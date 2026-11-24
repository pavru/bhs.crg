using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;
using static BHS.CRG.Tests.Plan.ScheduleScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class ScheduleWorkTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Ручная_работа_берёт_позицию_из_ядра_и_правится_только_с_версией_и_правом()
    {
        var scene = await CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var forms = $"{ScheduleUrl(schedule)}/works";
        var classifier = await EstimateSeed.ClassifierAsync(host);
        var employeeId = await EmployeeAsync(host);
        var input = new { volume = 10m, start = "2026-12-01", end = "2026-12-04", employeeIds = new[] { employeeId } };
        var work = await ReadAsync(scene.Editor.PostAsJsonAsync(forms,
            new { workTypeId = classifier.WorkType, sectionId = scene.Site.Section, work = input }));
        Assert.True(work.GetProperty("manual").GetBoolean());
        Assert.Equal(await EstimateSeed.PlanItemAsync(host, scene.Site), GuidOrNull(work, "planItemId"));
        var selected = await ReadAsync(scene.Editor.GetAsync($"{Root}?constructionId={scene.Site.Construction}"));
        Assert.Equal(Id(schedule), Id(Assert.Single(selected.EnumerateArray())));

        var path = WorkUrl(schedule, work);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(ScheduleUrl(schedule))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Put, path, Version(work), input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(scene.Editor, HttpMethod.Put, path, null, input)).StatusCode);
        var updated = await ReadAsync(SendAsync(scene.Editor, HttpMethod.Put, path, Version(work), input with { volume = 20m }));
        Assert.NotEqual(Version(work), Version(updated));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(scene.Editor, HttpMethod.Put, path, Version(work), input)).StatusCode);
        Assert.Equal(20m, Assert.Single(Works(await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(schedule)))))
            .GetProperty("volume").GetDecimal());

        var another = await EstimateSeed.SiteAsync(host);
        using var foreignSection = await scene.Editor.PostAsJsonAsync(forms,
            new { workTypeId = classifier.WorkType, sectionId = another.Section, work = input });
        Assert.Equal(HttpStatusCode.BadRequest, foreignSection.StatusCode);
    }

    [Fact]
    public async Task Архивный_исполнитель_остаётся_на_сохранённой_работе_и_отклоняется_в_новой()
    {
        var scene = await CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var forms = $"{ScheduleUrl(schedule)}/works";
        var classifier = await EstimateSeed.ClassifierAsync(host);
        var employeeId = await EmployeeAsync(host);
        var input = new { volume = 10m, employeeIds = new[] { employeeId } };
        var body = new { workTypeId = classifier.WorkType, work = input };
        var work = await ReadAsync(scene.Editor.PostAsJsonAsync(forms, body));
        await new WorkTypeSeed(host).ArchiveAsync(employeeId);

        var kept = await ReadAsync(SendAsync(scene.Editor, HttpMethod.Put, WorkUrl(schedule, work), Version(work),
            input with { volume = 12m }));
        Assert.Equal(employeeId, Assert.Single(kept.GetProperty("employeeIds").EnumerateArray()).GetGuid());
        using var refused = await scene.Editor.PostAsJsonAsync(forms, body);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("запись в архиве", await refused.Content.ReadAsStringAsync());
        Assert.Single(Works(await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(schedule)))));
    }

    internal static async Task<Guid> EmployeeAsync(PlanOnlyHost host)
    {
        var seed = new WorkTypeSeed(host);
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        if (!(await repo.FindAsync(t => t.Code == CoreRecordTypes.PersonCode)).Any())
            await seed.TypeAsync(CoreRecordTypes.PersonCode, "Лицо");
        var type = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)))!;
        var name = $"Исполнитель {Guid.NewGuid():N}";
        return (await seed.RecordAsync(type.Id, name)).Id;
    }
}
