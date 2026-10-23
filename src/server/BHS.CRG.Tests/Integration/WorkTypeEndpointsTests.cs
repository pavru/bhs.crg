using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Дверь видов работ <c>/api/work-types</c> (решение владельца WORK-59, ТЗ CORE-8): классификатор ведут
/// правом <c>core.worktypes.edit</c>, а не правкой общих данных, — и общий адрес его больше не пишет.
///
/// <para>Сторож самой задачи — сметчик, у которого нет <c>core.catalog.edit</c>, заводит вид работы.
/// Ради этого дверь и заведена: до неё право было у сметчика, а записать вид работы он не мог.</para>
/// </summary>
[Collection("Integration")]
public class WorkTypeEndpointsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkTypeSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Сметчик_без_правки_общих_данных_заводит_вид_работы()
    {
        var (unit, _) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wt");
        Assert.DoesNotContain(CorePermissions.CatalogEdit, (await AccessProbe.AccessAsync(estimator)).Permissions);

        var created = await estimator.PostAsJsonAsync("/api/work-types", new
        {
            displayName = "01.02 Прокладка кабеля",
            data = WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", metre),
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        var stored = await seed.SendAsync(new GetCommonDataEntryQuery(body.GetProperty("id").GetGuid()));
        Assert.Equal("01.02", stored!.Data.RootElement.GetProperty("Код").GetString());
    }

    /// <summary>
    /// Правка общих данных классификатор не открывает: право на него своё, и обратное («кто правит
    /// справочники, тот правит и виды работ») сделало бы его галкой без смысла.
    /// </summary>
    [Fact]
    public async Task Без_права_на_классификатор_пишущие_адреса_закрыты()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var work = await seed.RecordAsync(type.Id, "Монтаж", WorkTypeSeed.WorkTypeData("01", "Монтаж", await seed.RecordAsync(unit.Id, "шт")));
        var editor = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead, CorePermissions.CatalogEdit);
        editor.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", work.Version);

        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync("/api/work-types?purpose=display")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.PostAsJsonAsync("/api/work-types", new { displayName = "Х", data = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.PutAsJsonAsync($"/api/work-types/{work.Id}", new { displayName = "Х", data = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"/api/work-types/{work.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"/api/work-types/{work.Id}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"/api/work-types/{work.Id}/unarchive", null)).StatusCode);

        var after = await seed.SendAsync(new GetCommonDataEntryQuery(work.Id));
        Assert.Equal(work.Version, after!.Version);
    }

    /// <summary>
    /// Версия — на каждом пишущем адресе, кроме заведения: без неё 400, с устаревшей 409 словами вида
    /// работы. Архив тоже: общий адрес архивирует без версии, дверь этого послабления не наследует.
    /// </summary>
    [Fact]
    public async Task Пишущие_адреса_требуют_версию_и_отвергают_устаревшую()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var work = await seed.RecordAsync(type.Id, "Монтаж", WorkTypeSeed.WorkTypeData("01", "Монтаж", metre));
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wt");
        var data = WorkTypeSeed.WorkTypeData("01", "Монтаж лотков", metre);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await estimator.PutAsJsonAsync($"/api/work-types/{work.Id}", new { displayName = "Монтаж лотков", data })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.DeleteAsync($"/api/work-types/{work.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.PostAsync($"/api/work-types/{work.Id}/archive", null)).StatusCode);

        estimator.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", work.Version);
        var saved = await estimator.PutAsJsonAsync($"/api/work-types/{work.Id}", new { displayName = "Монтаж лотков", data });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Та же версия ещё раз — уже устаревшая: её сменила правка выше.
        foreach (var stale in new[]
        {
            await estimator.PutAsJsonAsync($"/api/work-types/{work.Id}", new { displayName = "Чужая", data }),
            await estimator.DeleteAsync($"/api/work-types/{work.Id}"),
            await estimator.PostAsync($"/api/work-types/{work.Id}/archive", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("Вид работы тем временем изменили", await stale.Content.ReadAsStringAsync());
        }

        var stored = await seed.SendAsync(new GetCommonDataEntryQuery(work.Id));
        Assert.Equal("Монтаж лотков", stored!.DisplayName);
        Assert.False(stored.IsArchived);
    }

    /// <summary>Архив дверью — с версией, и ответ несёт новую: «Вернуть» сразу следом не упирается в отказ.</summary>
    [Fact]
    public async Task Архив_дверью_отдаёт_новую_версию_для_возврата()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var work = await seed.RecordAsync(type.Id, "Монтаж", WorkTypeSeed.WorkTypeData("01", "Монтаж", await seed.RecordAsync(unit.Id, "м")));
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wt");

        var archived = await SendWithVersionAsync(estimator, HttpMethod.Post, $"/api/work-types/{work.Id}/archive", work.Version);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var version = (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;

        var back = await SendWithVersionAsync(estimator, HttpMethod.Post, $"/api/work-types/{work.Id}/unarchive", version);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.False((await seed.SendAsync(new GetCommonDataEntryQuery(work.Id)))!.IsArchived);
    }

    /// <summary>
    /// Общий адрес вид работы не заводит, не правит, не удаляет и не архивирует — 409 с адресом двери,
    /// запись не меняется. Иначе правило двери обходилось бы общим адресом под правкой общих данных.
    /// </summary>
    [Fact]
    public async Task Общий_адрес_вид_работы_не_пишет_и_называет_дверь()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var work = await seed.RecordAsync(type.Id, "Монтаж", WorkTypeSeed.WorkTypeData("01", "Монтаж", metre));
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wt");
        engineer.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", work.Version);

        foreach (var refused in new[]
        {
            await engineer.PostAsJsonAsync("/api/common-data", new
            {
                displayName = "02 Новый", compositeTypeId = type.Id, data = WorkTypeSeed.WorkTypeData("02", "Новый", metre),
                scope = "System",
            }),
            await engineer.PutAsJsonAsync($"/api/common-data/{work.Id}", new
            {
                displayName = "Подмена", data = WorkTypeSeed.WorkTypeData("01", "Подмена", metre),
            }),
            await engineer.DeleteAsync($"/api/common-data/{work.Id}"),
            await engineer.PostAsync($"/api/common-data/{work.Id}/archive", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("/api/work-types",
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("door").GetString());
        }

        var stored = await seed.SendAsync(new GetCommonDataEntryQuery(work.Id));
        Assert.Equal(work.Version, stored!.Version);
        Assert.Single(await seed.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: type.Id)));

        // Остальные записи общий адрес пишет как прежде — отказ стоит у вида работы, а не у адреса.
        var archivedUnit = await engineer.PostAsync($"/api/common-data/{metre.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archivedUnit.StatusCode);
    }

    /// <summary>
    /// Подтип вида работы — тоже вид работы: общий адрес отвергает и его запись, а дверь её правит.
    /// Клиент выбирает дверь по цепочке предков; разойдись он с сервером — запись подтипа не писалась
    /// бы ни одним адресом.
    /// </summary>
    [Fact]
    public async Task Подтип_вида_работы_пишется_только_дверью()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var sub = await seed.TypeAsync("ВидРаботыЭлектромонтаж", "Электромонтаж", parentId: type.Id);
        var metre = await seed.RecordAsync(unit.Id, "м");
        var work = await seed.RecordAsync(sub.Id, "Кабель", WorkTypeSeed.WorkTypeData("07", "Кабель", metre));
        var engineer = await AccessProbe.SignInAsync(fixture, SystemRoles.IdEngineer, "wts");
        engineer.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", work.Version);

        var common = await engineer.PutAsJsonAsync($"/api/common-data/{work.Id}", new
        {
            displayName = "Подмена", data = WorkTypeSeed.WorkTypeData("07", "Подмена", metre),
        });
        Assert.Equal(HttpStatusCode.Conflict, common.StatusCode);
        Assert.Equal(WorkTypeEndpoints.Door, (await common.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("door").GetString());

        var door = await engineer.PutAsJsonAsync($"/api/work-types/{work.Id}", new
        {
            displayName = "Кабель в лотке", data = WorkTypeSeed.WorkTypeData("07", "Кабель в лотке", metre),
        });
        Assert.Equal(HttpStatusCode.OK, door.StatusCode);

        // Заводит дверь запись именно подтипа, а тип чужого справочника не принимает вовсе.
        var created = await engineer.PostAsJsonAsync("/api/work-types", new
        {
            displayName = "08 Лоток", compositeTypeId = sub.Id, data = WorkTypeSeed.WorkTypeData("08", "Лоток", metre),
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(sub.Id, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compositeTypeId").GetGuid());
        var foreign = await engineer.PostAsJsonAsync("/api/work-types", new
        {
            displayName = "кг", compositeTypeId = unit.Id, data = "{}",
        });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    /// <summary>Чужая запись за дверью видов работ не находится — 404, а не правка организации правом на классификатор.</summary>
    [Fact]
    public async Task Чужая_запись_за_дверью_не_находится()
    {
        var (unit, _) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wt");
        estimator.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", metre.Version);

        Assert.Equal(HttpStatusCode.NotFound, (await estimator.GetAsync($"/api/work-types/{metre.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await estimator.PutAsJsonAsync($"/api/work-types/{metre.Id}", new { displayName = "Х", data = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await estimator.DeleteAsync($"/api/work-types/{metre.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await estimator.PostAsync($"/api/work-types/{metre.Id}/archive", null)).StatusCode);
    }

    /// <summary>
    /// Список двери — по назначению, как общий адрес (ТЗ CORE-34.4): без назначения отказ, в выборе
    /// архивного вида нет, в показе он на месте. Поиск — по коду и наименованию.
    /// </summary>
    [Fact]
    public async Task Список_двери_по_назначению_и_поиску()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var live = await seed.RecordAsync(type.Id, "Прокладка", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", metre));
        var gone = await seed.RecordAsync(type.Id, "Демонтаж", WorkTypeSeed.WorkTypeData("09.01", "Демонтаж", metre));
        await seed.ArchiveAsync(gone.Id);
        var reader = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead);

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/work-types")).StatusCode);
        Assert.Equal([live.Id], await IdsAsync(reader, "/api/work-types?purpose=choice"));
        Assert.Equal(new[] { live.Id, gone.Id }.Order(), (await IdsAsync(reader, "/api/work-types?purpose=display")).Order());
        Assert.Equal([live.Id], await IdsAsync(reader, "/api/work-types?purpose=display&q=01.0"));
        Assert.Equal([live.Id], await IdsAsync(reader, "/api/work-types?purpose=display&q=КАБЕЛ"));
    }

    private static async Task<Guid[]> IdsAsync(HttpClient client, string url) =>
        [.. (await client.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid())];

    private static Task<HttpResponseMessage> SendWithVersionAsync(HttpClient client, HttpMethod method, string url, string version)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }
}
