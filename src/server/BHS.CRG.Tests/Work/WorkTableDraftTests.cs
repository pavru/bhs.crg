using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Tests.Integration;
using static BHS.CRG.Tests.Work.WorkTableWorld;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class WorkTableDraftTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly WorkTableWorld world = new(host);

    public Task InitializeAsync() => world.Assignments.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Чужой_черновик_скрыт_в_клетках_отборе_итогах_и_расшифровке_до_подачи()
    {
        var employeeType = await world.Assignments.DirectoryAsync();
        var site = await world.Assignments.SiteAsync("Стройка А");
        var (employee, installer) = await world.InstallerAsync(employeeType, "Монтажник А");
        var (_, manager) = await world.ManagerAsync(employeeType, site);
        var (type, unit, _) = await world.ClassifierAsync();
        var draft = await world.ReportAsync(employee, site, type, unit, "Скрытое место", 8, false, 12);

        var presence = await manager.GetFromJsonAsync<JsonElement>($"{Reports}?row={draft.Id}&totals=Часы,ЧислоСтрок");
        var masked = Assert.Single(presence.GetProperty("rows").EnumerateArray());
        Assert.Equal("Черновик", masked.GetProperty("Состояние").GetString());
        Assert.Equal(Window, DateTimeOffset.Parse(masked.GetProperty("МоментПодачи").GetString()!));
        Assert.All(masked.EnumerateObject().Where(p => p.Name is not ("Состояние" or "МоментПодачи")),
            p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        Assert.Equal(JsonValueKind.Null, presence.GetProperty("breakdown").ValueKind);
        Assert.All(presence.GetProperty("totals").EnumerateObject(), t =>
            Assert.Equal(0, t.Value.GetProperty("count").GetInt32()));

        var filter = Uri.EscapeDataString("""{"type":"condition","column":"Часы","op":"eq","value":"8"}""");
        var hours = await manager.GetFromJsonAsync<JsonElement>($"{Reports}?filter={filter}");
        Assert.Equal(0, hours.GetProperty("count").GetInt32());
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement>(Lines)).GetProperty("rows").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{Lines}?row={draft.Lines[0].Id}")).StatusCode);

        var own = await installer.GetFromJsonAsync<JsonElement>($"{Reports}?row={draft.Id}");
        Assert.Equal(8, Assert.Single(own.GetProperty("rows").EnumerateArray()).GetProperty("Часы").GetDecimal());
        Assert.Single(own.GetProperty("breakdown").GetProperty("rows").EnumerateArray());
        Assert.Single((await installer.GetFromJsonAsync<JsonElement>(Lines)).GetProperty("rows").EnumerateArray());

        await world.SubmitAsync(draft.Id);

        var full = await manager.GetFromJsonAsync<JsonElement>($"{Reports}?row={draft.Id}");
        var submitted = Assert.Single(full.GetProperty("rows").EnumerateArray());
        Assert.Equal("Подан", submitted.GetProperty("Состояние").GetString());
        Assert.Equal(AssignmentHost.Now, DateTimeOffset.Parse(submitted.GetProperty("МоментПодачи").GetString()!));
        Assert.Equal(8, submitted.GetProperty("Часы").GetDecimal());
        Assert.Single(full.GetProperty("breakdown").GetProperty("rows").EnumerateArray());
        Assert.Single((await manager.GetFromJsonAsync<JsonElement>(Lines)).GetProperty("rows").EnumerateArray());
    }
}
