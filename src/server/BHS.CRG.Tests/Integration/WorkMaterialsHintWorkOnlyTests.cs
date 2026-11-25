using System.Net;
using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public sealed class WorkMaterialsHintWorkOnlyTests(AssignmentHost host)
{
    private readonly AssignmentWorld world = new(host);

    [Fact]
    public async Task Своя_стройка_и_отсутствующие_поставщики()
    {
        await world.ResetAsync();
        var people = await world.DirectoryAsync();
        var employee = await world.EmployeeAsync(people, "Монтажник подсказки");
        var mine = await world.SiteAsync("Стройка монтажника");
        var other = await world.SiteAsync("Чужая стройка");
        var (user, client) = await world.UserAsync("Монтажник", WorkModule.ReportOwn);
        using (client)
        {
            await world.LinkAsync(employee, user);
            await world.AssignedAsync(employee, mine, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
            using var own = await client.GetAsync(Path(mine));
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var result = await JsonAsync(own);
            Assert.Equal("not-connected", State(result, "issued"));
            Assert.Equal("not-connected", State(result, "estimate"));
            Assert.Equal("not-connected", State(result, "catalog"));
            Assert.Equal("Справочник номенклатуры не заведён.", result.GetProperty("catalog").GetProperty("reason").GetString());
            using var foreign = await client.GetAsync(Path(other));
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            using var noAssignmentOnDate = await client.GetAsync(Path(mine) + "&date=2026-12-01");
            Assert.Equal(HttpStatusCode.NotFound, noAssignmentOnDate.StatusCode);
        }

        var (_, readOnly) = await world.UserAsync("Руководитель", WorkModule.ReportRead);
        using (readOnly)
        using (var response = await readOnly.GetAsync(Path(other)))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (_, withReadScope) = await world.UserAsync("Руководитель и монтажник", WorkModule.ReportRead, WorkModule.ReportOwn);
        using (withReadScope)
        using (var response = await withReadScope.GetAsync(Path(other)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Неполный_справочник_и_архив_обозначены()
    {
        await world.ResetAsync();
        var mine = await world.SiteAsync("Стройка для поиска");
        var seed = new WorkTypeSeed(host);
        var materialType = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        using (var scope = host.Services.CreateScope())
        {
            await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(Enumerable.Range(1, 102).Select(i => DomainObject.Create(materialType.Id,
                $"Гофра {i:000}", JsonDocument.Parse("{}"), CatalogScope.System, null)));
            await db.SaveChangesAsync();
        }
        var archived = await seed.RecordAsync(materialType.Id, "Гофра архивная");
        await seed.ArchiveAsync(archived.Id);

        var (_, manager) = await world.UserAsync("Менеджер всех строек", WorkModule.ReportReview, WorkModule.SitesAll);
        using (manager)
        using (var response = await manager.GetAsync(Path(mine) + "&q=" + Uri.EscapeDataString("гофра")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var catalog = (await JsonAsync(response)).GetProperty("catalog");
            Assert.Equal("ok", catalog.GetProperty("state").GetString());
            Assert.True(catalog.GetProperty("more").GetBoolean());
            Assert.Equal(1, catalog.GetProperty("inArchive").GetInt32());
            var items = catalog.GetProperty("items");
            Assert.Equal(100, items.GetArrayLength());
            Assert.DoesNotContain(items.EnumerateArray(), item => item.GetProperty("id").GetGuid() == archived.Id);
        }
    }

    internal static string Path(Guid constructionId) => $"/api/work/materials/hint?constructionId={constructionId}";

    private static string? State(JsonElement result, string tier) => result.GetProperty(tier).GetProperty("state").GetString();
}
