using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Documents;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Дверь вариантов вида работ <c>/api/work-variants</c> ([WORK-49] [AUTH-9], change
/// <c>work-06-work-type-variants</c>): варианты ведут правом классификатора <c>core.worktypes.edit</c>,
/// ссылка варианта на вид постоянна, наименование уникально в пределах вида, а общий адрес справочников
/// записи вариантов не пишет. Здесь — заведение, права, версия и правило записи; удаление, архив и
/// общий адрес — в <c>WorkVariantEndpointsTests.Archive.cs</c>.
/// </summary>
[Collection("Integration")]
public partial class WorkVariantEndpointsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkVariantSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ: сметчик без правки общих данных заводит вариант, ответ называет вид, а список вида
    /// на выбор его показывает — и не показывает вариантов чужого вида.
    /// </summary>
    [Fact]
    public async Task Сметчик_заводит_вариант_и_видит_его_среди_вариантов_вида()
    {
        var (variantType, cable, wire) = await seed.SetUpAsync();
        var other = await seed.VariantAsync(variantType, wire, "в трубе");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        Assert.DoesNotContain(CorePermissions.CatalogEdit, (await AccessProbe.AccessAsync(estimator)).Permissions);

        var created = await estimator.PostAsJsonAsync(WorkVariantEndpoints.Door, new
        {
            displayName = "в лотке", data = WorkVariantSeed.Data(cable, "в лотке"),
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        Assert.Equal(cable.Id, body.GetProperty("workTypeId").GetGuid());
        var id = body.GetProperty("id").GetGuid();

        var listed = await IdsAsync(estimator, $"{WorkVariantEndpoints.Door}?workTypeId={cable.Id}&purpose=choice");
        Assert.Equal([id], listed);
        Assert.DoesNotContain(other.Id, listed);
    }

    /// <summary>Правка общих данных классификатор не открывает: у вариантов — то же право, что у видов работ.</summary>
    [Fact]
    public async Task С_одной_правкой_общих_данных_пишущие_адреса_закрыты()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var editor = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead, CorePermissions.CatalogEdit);
        editor.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", variant.Version);
        var data = WorkVariantSeed.Data(cable, "в трубе");

        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{WorkVariantEndpoints.Door}?purpose=display")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{WorkVariantEndpoints.Door}/{variant.Id}")).StatusCode);
        foreach (var refused in new[]
        {
            await editor.PostAsJsonAsync(WorkVariantEndpoints.Door, new { displayName = "в трубе", data }),
            await editor.PutAsJsonAsync($"{WorkVariantEndpoints.Door}/{variant.Id}", new { displayName = "в трубе", data }),
            await editor.DeleteAsync($"{WorkVariantEndpoints.Door}/{variant.Id}"),
            await editor.PostAsync($"{WorkVariantEndpoints.Door}/{variant.Id}/archive", null),
            await editor.PostAsync($"{WorkVariantEndpoints.Door}/{variant.Id}/unarchive", null),
        })
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Assert.Equal(variant.Version, (await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.Version);
    }

    /// <summary>Версия — на каждом пишущем адресе, кроме заведения: без неё 400, с устаревшей 409 словами варианта.</summary>
    [Fact]
    public async Task Пишущие_адреса_требуют_версию_и_отвергают_устаревшую()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var url = $"{WorkVariantEndpoints.Door}/{variant.Id}";
        var data = WorkVariantSeed.Data(cable, "в лотке перфорированном");

        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.PutAsJsonAsync(url, new { displayName = "в лотке", data })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.PostAsync($"{url}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await estimator.PostAsync($"{url}/unarchive", null)).StatusCode);

        estimator.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", variant.Version);
        Assert.Equal(HttpStatusCode.OK,
            (await estimator.PutAsJsonAsync(url, new { displayName = "в лотке перфорированном", data })).StatusCode);

        // Та же версия ещё раз — уже устаревшая: её сменила правка выше.
        foreach (var stale in new[]
        {
            await estimator.PutAsJsonAsync(url, new { displayName = "чужая", data = WorkVariantSeed.Data(cable, "чужая") }),
            await estimator.DeleteAsync(url),
            await estimator.PostAsync($"{url}/archive", null),
            await estimator.PostAsync($"{url}/unarchive", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("Вариант тем временем изменили", await stale.Content.ReadAsStringAsync());
        }

        var stored = await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id));
        Assert.Equal("в лотке перфорированном", stored!.DisplayName);
        Assert.False(stored.IsArchived);
    }

    /// <summary>Вид у варианта постоянен: перенос — отказ с полем, запись не меняется.</summary>
    [Fact]
    public async Task Перенос_варианта_на_другой_вид_отказ()
    {
        var (variantType, cable, wire) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в лотке");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        estimator.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", variant.Version);

        var moved = await estimator.PutAsJsonAsync($"{WorkVariantEndpoints.Door}/{variant.Id}",
            new { displayName = "в лотке", data = WorkVariantSeed.Data(wire, "в лотке") });

        await AssertRefusedAsync(moved, HttpStatusCode.Conflict, CoreRecordTypes.WorkVariantWorkTypeKey, "не переносится");
        Assert.Equal(variant.Version, (await seed.Types.SendAsync(new GetCommonDataEntryQuery(variant.Id)))!.Version);
    }

    /// <summary>К архивному виду новый вариант не заводится (CORE-34.4); отказ называет поле.</summary>
    [Fact]
    public async Task Вариант_к_архивному_виду_работы_отказ_с_полем()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        await seed.Types.ArchiveAsync(cable.Id);
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var refused = await estimator.PostAsJsonAsync(WorkVariantEndpoints.Door,
            new { displayName = "в лотке", data = WorkVariantSeed.Data(cable, "в лотке") });

        await AssertRefusedAsync(refused, HttpStatusCode.Conflict, CoreRecordTypes.WorkVariantWorkTypeKey, "в архиве");
        Assert.Empty(await seed.Types.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: variantType.Id)));
    }

    /// <summary>
    /// Вид работы — ссылкой на запись классификатора: вложенная копия, пустое поле и ссылка на запись
    /// другого справочника — отказ с именем поля. Наименование обязательно.
    /// </summary>
    [Fact]
    public async Task Вид_ссылкой_на_классификатор_и_наименование_обязательны()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var unit = (await seed.Types.SendAsync(new ListDocumentTypesQuery())).Single(t => t.Code == CoreRecordTypes.UnitCode);
        var piece = await seed.Types.RecordAsync(unit.Id, "шт");
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        var key = CoreRecordTypes.WorkVariantWorkTypeKey;

        var inline = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [key] = new Dictionary<string, object> { ["Код"] = "01.02", ["Наименование"] = "Прокладка кабеля" },
            [CoreRecordTypes.WorkVariantNameKey] = "в лотке",
        });
        await AssertRefusedAsync(await PostAsync(estimator, inline), HttpStatusCode.BadRequest, key, "копией записи");
        await AssertRefusedAsync(await PostAsync(estimator, """{"Наименование":"в лотке"}"""), HttpStatusCode.BadRequest, key, "Не указан вид");
        await AssertRefusedAsync(await PostAsync(estimator, WorkVariantSeed.Data(piece, "в лотке")), HttpStatusCode.BadRequest, key, "не найден");
        await AssertRefusedAsync(await PostAsync(estimator, WorkVariantSeed.Data(cable, "  ")),
            HttpStatusCode.BadRequest, CoreRecordTypes.WorkVariantNameKey, "наименование");

        Assert.Empty(await seed.Types.SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: variantType.Id)));
    }

    /// <summary>
    /// Наименование уникально в пределах ВИДА: второй живой «в лотке» того же вида — 409 с названием
    /// существующего, а «в лотке» другого вида — законный вариант, даже если у первого вида такой в архиве.
    /// </summary>
    [Fact]
    public async Task Дубль_в_виде_отказ_а_одноимённый_у_другого_вида_заводится()
    {
        var (variantType, cable, wire) = await seed.SetUpAsync();
        var existing = await seed.VariantAsync(variantType, cable, "в лотке");
        var gone = await seed.VariantAsync(variantType, cable, "в трубе");
        await seed.Types.ArchiveAsync(gone.Id);
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var duplicate = await AssertRefusedAsync(await PostAsync(estimator, WorkVariantSeed.Data(cable, "В лотке ")),
            HttpStatusCode.Conflict, CoreRecordTypes.WorkVariantNameKey, "«в лотке»");
        Assert.Equal(existing.Id, duplicate.GetProperty("existingId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(estimator, WorkVariantSeed.Data(wire, "в лотке"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(estimator, WorkVariantSeed.Data(wire, "в трубе"))).StatusCode);
    }

    /// <summary>
    /// Такой вариант этого вида лежит в архиве — тот же ответ, что у общего адреса: экран предлагает
    /// вернуть его. Подтверждённое заведение (<c>createAnyway</c>) проходит.
    /// </summary>
    [Fact]
    public async Task Архивный_вариант_своего_вида_предлагается_вернуть()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var gone = await seed.VariantAsync(variantType, cable, "в лотке");
        await seed.Types.ArchiveAsync(gone.Id);
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");

        var twin = await PostAsync(estimator, WorkVariantSeed.Data(cable, "в лотке"));
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        var body = await twin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("archived-twin", body.GetProperty("code").GetString());
        Assert.Equal(gone.Id, body.GetProperty("archivedId").GetGuid());

        var anyway = await estimator.PostAsJsonAsync(WorkVariantEndpoints.Door,
            new { displayName = "в лотке", data = WorkVariantSeed.Data(cable, "в лотке"), createAnyway = true });
        Assert.Equal(HttpStatusCode.OK, anyway.StatusCode);
    }

    /// <summary>Список — по назначению (CORE-34.4): без него отказ, в выборе архивного нет, в показе он с признаком.</summary>
    [Fact]
    public async Task Список_двери_по_назначению()
    {
        var (variantType, cable, _) = await seed.SetUpAsync();
        var live = await seed.VariantAsync(variantType, cable, "в лотке");
        var gone = await seed.VariantAsync(variantType, cable, "в трубе");
        await seed.Types.ArchiveAsync(gone.Id);
        var reader = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead);

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(WorkVariantEndpoints.Door)).StatusCode);
        Assert.Equal([live.Id], await IdsAsync(reader, $"{WorkVariantEndpoints.Door}?purpose=choice&workTypeId={cable.Id}"));
        var shown = await reader.GetFromJsonAsync<JsonElement>($"{WorkVariantEndpoints.Door}?purpose=display&workTypeId={cable.Id}");
        Assert.Equal(new[] { live.Id, gone.Id }.Order(), shown.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Order());
        Assert.True(shown.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == gone.Id).GetProperty("archived").GetBoolean());
    }

    /// <summary>
    /// Справочник не заведён — отказ с причиной, а не пустой список (CORE-20.4), тем же телом, что у видов
    /// работ: нет единиц — нет и классификатора; классификатор есть, а вариантов ещё нет — «появится при запуске».
    /// </summary>
    [Fact]
    public async Task Без_справочника_отказ_directory_missing_с_причиной()
    {
        var client = await GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.CatalogRead, CorePermissions.WorkTypesEdit);

        await AssertDirectoryMissingAsync(await client.GetAsync($"{WorkVariantEndpoints.Door}?purpose=choice"), WorkTypeEndpoints.UnitMissing);
        await AssertDirectoryMissingAsync(
            await client.PostAsJsonAsync(WorkVariantEndpoints.Door, new { displayName = "в лотке", data = "{}" }), WorkTypeEndpoints.UnitMissing);

        await seed.Types.ClassifierAsync();
        await AssertDirectoryMissingAsync(await client.GetAsync($"{WorkVariantEndpoints.Door}?purpose=display"), WorkTypeEndpoints.NotCreated);

        await seed.Types.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant));
        var empty = await client.GetAsync($"{WorkVariantEndpoints.Door}?purpose=choice");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    /// <summary>Чужая запись за дверью вариантов не находится — 404, а не правка вида работы правом на варианты.</summary>
    [Fact]
    public async Task Чужая_запись_за_дверью_не_находится()
    {
        var (_, cable, _) = await seed.SetUpAsync();
        var estimator = await AccessProbe.SignInAsync(fixture, "Estimator", "wv");
        estimator.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", cable.Version);
        var url = $"{WorkVariantEndpoints.Door}/{cable.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await estimator.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await estimator.PutAsJsonAsync(url, new { displayName = "Х", data = WorkVariantSeed.Data(cable, "Х") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await estimator.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await estimator.PostAsync($"{url}/archive", null)).StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string data) =>
        client.PostAsJsonAsync(WorkVariantEndpoints.Door, new { displayName = "вариант", data });

    /// <summary>Отказ с названным полем и словами причины; тело — звавшему, если ему нужно больше.</summary>
    private static async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string field, string words)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.Contains(words, body.GetProperty("error").GetString(), StringComparison.CurrentCultureIgnoreCase);
        return body;
    }

    private static async Task AssertDirectoryMissingAsync(HttpResponseMessage response, string reason)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("directory-missing", body.GetProperty("code").GetString());
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
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
