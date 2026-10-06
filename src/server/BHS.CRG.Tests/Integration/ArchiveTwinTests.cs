using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Есть в архиве — вернуть?» на стороне сервера (ТЗ CORE-34.4, issue #1185, шаг 4б): создание записи,
/// чей ключ идентичности совпал с архивной, и вопрос формы «какие из моих ссылок — в архиве».
///
/// <para>Отказ серверный, потому что создают не только с формы: без него дубль архивной записи
/// заводит любой, кто не нашёл её в списке выбора, — то есть ровно тот, ради кого архив и сделан.</para>
///
/// <para>Каждый шаг — в своей области служб: резолвер держит индекс кандидатов всё время жизни
/// области, и признак архива, поставленный после первого обращения, он бы уже не увидел.</para>
/// </summary>
[Collection("Integration")]
public class ArchiveTwinTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private const string OrgSchema = "{'fields':[{'key':'ИНН','type':'string','tags':['identity']},{'key':'Адрес','type':'string'}]}";

    private async Task<T> InScopeAsync<T>(Func<IServiceScope, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();
        return await work(scope);
    }

    private Task<Guid> TypeAsync(string code, string schema) => InScopeAsync(async s =>
        (await M(s).Send(new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Composite, null, J(schema)))).Id);

    private Task<Guid> EntryAsync(Guid typeId, string name, string data, bool anyway = false) => InScopeAsync(async s =>
        (await M(s).Send(new CreateCommonDataEntryCommand(
            name, typeId, J(data), CatalogScope.System, null, CreateAnyway: anyway))).Id);

    private Task ArchiveAsync(Guid id) => InScopeAsync(async s =>
    {
        Assert.Equal(ArchiveOutcome.Changed,
            await s.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, true));
        return 0;
    });

    [Fact]
    public async Task Создание_с_ключом_архивной_записи_отвергнуто_и_называет_её()
    {
        var type = await TypeAsync("TWIN_A", OrgSchema);
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}");
        await ArchiveAsync(archived);

        // Название другое — совпал КЛЮЧ: это та же организация, заведённая заново.
        var refusal = await Assert.ThrowsAsync<ArchivedTwinException>(
            () => EntryAsync(type, "ООО «Ромашка»", "{'ИНН':'7701'}"));

        Assert.Equal(archived, refusal.ArchivedId);
        Assert.Equal("Ромашка", refusal.ArchivedName);
    }

    [Fact]
    public async Task Создать_всё_равно_создаёт()
    {
        var type = await TypeAsync("TWIN_B", OrgSchema);
        await ArchiveAsync(await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}"));

        var created = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}", anyway: true);

        Assert.NotEqual(Guid.Empty, created);
    }

    /// <summary>
    /// Действующая запись с тем же ключом есть — значит, дело не в архиве: действующих тёзок ядро не
    /// запрещает, и отказ здесь называл бы причиной архив, который ни при чём.
    /// </summary>
    [Fact]
    public async Task Действующая_запись_с_тем_же_ключом_отказа_не_даёт()
    {
        var type = await TypeAsync("TWIN_C", OrgSchema);
        await ArchiveAsync(await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}"));
        await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}", anyway: true);

        var third = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}");

        Assert.NotEqual(Guid.Empty, third);
    }

    [Fact]
    public async Task По_одному_названию_отказа_нет()
    {
        var type = await TypeAsync("TWIN_D", OrgSchema);
        await ArchiveAsync(await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}"));

        // Тёзка с другим ключом и запись без ключа вовсе — обе законны.
        Assert.NotEqual(Guid.Empty, await EntryAsync(type, "Ромашка", "{'ИНН':'7702'}"));
        Assert.NotEqual(Guid.Empty, await EntryAsync(type, "Ромашка", "{'Адрес':'Москва'}"));
    }

    /// <summary>
    /// Вопрос «нет ли такой в архиве» задаётся ДО записи, а резолвер помнит кандидатов всё время
    /// жизни области служб. Спроси создание обычным путём — и запись, только что созданная, в этой
    /// же области не находилась бы: вставка, которая заводит запись и тут же на неё ссылается,
    /// получила бы «не найдено». Так и было в первой редакции; поймал полный прогон.
    /// </summary>
    [Fact]
    public async Task Созданная_запись_находится_резолвером_в_той_же_области_служб()
    {
        var type = await TypeAsync("TWIN_G", OrgSchema);

        var (created, found) = await InScopeAsync(async s =>
        {
            var id = (await M(s).Send(new CreateCommonDataEntryCommand(
                "Лютик", type, J("{'ИНН':'5'}"), CatalogScope.System, null))).Id;
            var match = await s.ServiceProvider.GetRequiredService<BHS.CRG.Application.Resolution.IObjectResolver>()
                .ResolveAsync(BHS.CRG.Application.Resolution.ObjectMatchRequest.ByName(type, "Лютик"), CatalogScope.System, null);
            return (id, match);
        });

        Assert.Equal(created, found?.Id);
    }

    /// <summary>
    /// Экрану нужны поля, а не фраза: по ним он предлагает «вернуть из архива» и повторяет создание.
    /// Адрес собирает ответ своей формой — и однажды такое поле уже не доехало (см. резолвер).
    /// </summary>
    [Fact]
    public async Task Адрес_создания_отвечает_409_с_записью_и_принимает_согласие()
    {
        var type = await TypeAsync("TWIN_E", OrgSchema);
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}");
        await ArchiveAsync(archived);
        var client = await SignInAsync();
        object Body(bool? anyway) => new
        {
            displayName = "Ромашка", compositeTypeId = type, data = "{\"ИНН\":\"7701\"}",
            scope = "System", createAnyway = anyway,
        };

        var refused = await client.PostAsJsonAsync("/api/common-data", Body(null));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("archived-twin", body.GetProperty("code").GetString());
        Assert.Equal(archived, body.GetProperty("archivedId").GetGuid());
        Assert.Equal("Ромашка", body.GetProperty("archivedName").GetString());
        Assert.Equal("System", body.GetProperty("archivedScope").GetString());
        Assert.Contains("в архиве", body.GetProperty("error").GetString());

        var agreed = await client.PostAsJsonAsync("/api/common-data", Body(true));
        Assert.Equal(HttpStatusCode.OK, agreed.StatusCode);
    }

    [Fact]
    public async Task Адрес_называет_архивные_среди_стоящих_ссылок()
    {
        var type = await TypeAsync("TWIN_F", OrgSchema);
        var live = await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'2'}");
        await ArchiveAsync(archived);
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync("/api/common-data/archived-among",
            new { ids = new[] { live, archived, Guid.NewGuid() } });

        response.EnsureSuccessStatusCode();
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("archived").EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Equal([archived], ids);
    }

    /// <summary>
    /// Раздел «В архиве» окна выбора просит одни архивные — не весь список уровня. С выбором
    /// параметр отвергается: у выбора архивных нет, и пустой ответ читался бы как «в архиве пусто».
    /// </summary>
    [Fact]
    public async Task Список_уровня_отдаёт_только_архивные_по_просьбе_и_только_для_показа()
    {
        var type = await TypeAsync("TWIN_H", OrgSchema);
        await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'2'}");
        await ArchiveAsync(archived);
        var client = await SignInAsync();
        var url = $"/api/common-data/for-scope?scope=System&typeId={type}";

        var only = await client.GetFromJsonAsync<JsonElement>(url + "&purpose=display&only=archived");

        Assert.Equal([archived], only.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url + "&purpose=choice&only=archived")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url + "&purpose=display&only=live")).StatusCode);
    }

    private async Task<HttpClient> SignInAsync()
    {
        var email = $"twin_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Twin";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(
                new ApplicationUser { UserName = email, Email = email, DisplayName = "Т", EmailConfirmed = true },
                password)).Succeeded);
            await users.AddToRoleAsync((await users.FindByEmailAsync(email))!, BHS.CRG.Api.Auth.SystemRoles.IdEngineer);
        }
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
