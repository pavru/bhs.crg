using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Владелец-модуль у типа (issue #955, ТЗ CORE-18, CORE-30, TYPE-5).
///
/// Главное правило, которое здесь стережётся: **опора типа ядра принадлежит ядру.** Опора — это
/// родитель и вложение (<c>complex</c>, <c>array</c>): без них тип не описать вовсе, и при
/// выключенном модуле тип ядра остался бы без родителя, никуда при этом не делся.
///
/// ⚠️ Документная ссылка (<c>doc-ref</c>) опорой НЕ считается — и это решение, а не упущение,
/// поэтому у него здесь свой тест. Считай мы её опорой, ядру пришлось бы забрать документные типы
/// исполнительной документации (через поле профиля раздела и приказ подписанта), и признак
/// владельца перестал бы что-либо значить ровно там, ради чего заводится.
/// </summary>
[Collection("Integration")]
public class TypeOwnershipTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Module = "id";

    // ── Опора ядра — ядро ─────────────────────────────────────────────────────

    [Fact]
    public async Task Тип_ядра_не_наследуется_от_типа_модуля()
    {
        var module = await TypeAsync("MOD_PARENT", Module);
        var core = await TypeAsync("CORE_CHILD", TypeOwner.Core);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new UpdateDocumentTypeCommand(core.Id, core.Name, core.Code, module.Id)));

        Assert.Contains("родитель", refusal.Message);
        Assert.Contains("MOD_PARENT", refusal.Message);
        Assert.Contains("ядру", refusal.Message);

        // И правка действительно не доехала: отказ, который «почти сохранил», был бы хуже молчания.
        Assert.Null((await SendAsync(new GetDocumentTypeQuery(core.Id)))!.ParentId);
    }

    [Fact]
    public async Task Тип_ядра_не_вкладывает_тип_модуля()
    {
        var module = await TypeAsync("MOD_NESTED", Module);
        var core = await TypeAsync("CORE_HOLDER", TypeOwner.Core);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new UpdateDocumentTypeSchemaCommand(core.Id, Embedding("Вложение", "complex", module.Id))));

        Assert.Contains("поле «Вложение»", refusal.Message);
        Assert.Contains("MOD_NESTED", refusal.Message);
    }

    /// <summary>
    /// Массив чужих значений — то же вложение: форма типа включает в себя чужую форму, и «много»
    /// вместо «одного» ничего в этом не меняет. Отдельный тест, потому что вид поля другой, и
    /// проверка, написанная на один <c>complex</c>, прошла бы мимо.
    /// </summary>
    [Fact]
    public async Task Тип_ядра_не_вкладывает_массив_типа_модуля()
    {
        var module = await TypeAsync("MOD_ARRAY", Module);
        var core = await TypeAsync("CORE_ARRAY_HOLDER", TypeOwner.Core);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new UpdateDocumentTypeSchemaCommand(core.Id, Embedding("Строки", "array", module.Id))));

        Assert.Contains("MOD_ARRAY", refusal.Message);
    }

    /// <summary>
    /// Документная ссылка из ядра в модуль РАЗРЕШЕНА. Живой пример такой связи в системе уже есть:
    /// профиль раздела (ядро) ссылается на документ проекта (исполнительная документация).
    /// </summary>
    [Fact]
    public async Task Документная_ссылка_из_ядра_в_модуль_разрешена()
    {
        var module = await TypeAsync("MOD_DOC", Module, DocumentTypeKind.Document);
        var core = await TypeAsync("CORE_REFERRER", TypeOwner.Core);

        var saved = await SendAsync(
            new UpdateDocumentTypeSchemaCommand(core.Id, Embedding("Проект", "doc-ref", module.Id)));

        Assert.Contains("doc-ref", saved.Schema.RootElement.GetRawText());
    }

    [Fact]
    public async Task Тип_модуля_опирается_на_тип_ядра_свободно()
    {
        var core = await TypeAsync("CORE_BASE", TypeOwner.Core);
        var module = await TypeAsync("MOD_DERIVED", Module);

        var saved = await SendAsync(new UpdateDocumentTypeCommand(module.Id, module.Name, module.Code, core.Id));

        Assert.Equal(core.Id, saved.ParentId);
    }

    /// <summary>
    /// Вторая дверь, и она менее заметна: правило обходится не только со стороны типа ядра, но и
    /// со стороны его ОПОРЫ — достаточно отдать опору модулю, и тип ядра остаётся ни с чем тем же
    /// действием. Проверь мы одно направление, второе осталось бы открытым.
    /// </summary>
    [Fact]
    public async Task Опору_типа_ядра_нельзя_отдать_модулю()
    {
        var support = await TypeAsync("CORE_SUPPORT", TypeOwner.Core);
        var core = await TypeAsync("CORE_DEPENDENT", TypeOwner.Core);
        await SendAsync(new UpdateDocumentTypeCommand(core.Id, core.Name, core.Code, support.Id));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new SetDocumentTypeOwnerCommand(support.Id, Module)));

        Assert.Contains("CORE_DEPENDENT", refusal.Message);
        Assert.Equal(TypeOwner.Core, (await SendAsync(new GetDocumentTypeQuery(support.Id)))!.Module);
    }

    /// <summary>Передача владельца — ради неё адрес и заведён: без неё чинить принадлежность нечем.</summary>
    [Fact]
    public async Task Тип_передаётся_другому_владельцу()
    {
        var type = await TypeAsync("CORE_MOVABLE", TypeOwner.Core);

        var moved = await SendAsync(new SetDocumentTypeOwnerCommand(type.Id, Module));

        Assert.Equal(Module, moved.Module);
    }

    // ── Владелец обязателен и проверяется по экземпляру ────────────────────────

    [Fact]
    public async Task Без_владельца_тип_не_заводится()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/document-types", new
        {
            name = "Без владельца", code = "NO_OWNER", kind = "Composite",
            schema = """{"fields":[]}""", module = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("владелец", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Владельцем можно назвать только то, что на экземпляре есть. Иначе тип уехал бы к модулю,
    /// которого здесь нет, и исчез бы из редактора тем же действием, каким его отдавали.
    /// </summary>
    [Fact]
    public async Task Владельцем_не_становится_модуль_которого_нет()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/document-types", new
        {
            name = "Чужой модуль", code = "ALIEN", kind = "Composite",
            schema = """{"fields":[]}""", module = "work",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("core", text);
        Assert.Contains("id", text);
    }

    [Fact]
    public async Task Заведённый_человеком_тип_достаётся_ядру_и_общий()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/document-types", new
        {
            name = "Справочник", code = "BY_HAND", kind = "Composite",
            schema = """{"fields":[]}""", module = TypeOwner.Core,
        });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TypeOwner.Core, created.GetProperty("module").GetString());
        // Общий, а не закрытый: заводят такой тип затем, чтобы его объекты попали в общие данные.
        Assert.Equal(nameof(TypeVisibility.Shared), created.GetProperty("visibility").GetString());
    }

    // ── Способ хранения ───────────────────────────────────────────────────────

    /// <summary>
    /// Запись модуля общим путём не заводится (ТЗ CORE-16). Таких типов сегодня нет ни одного —
    /// тип для этой проверки заводится прямо в базе, потому что через редактор способ хранения не
    /// назначается: его объявляет модуль, а модулей с записями ещё нет.
    /// </summary>
    [Fact]
    public async Task Объект_записи_модуля_общим_путём_не_заводится()
    {
        Guid typeId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var type = DocumentType.Create(
                "Запись модуля", "MOD_ROW", DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""),
                Module, TypeVisibility.Closed, storage: TypeStorage.ModuleTable);
            db.DocumentTypes.Add(type);
            await db.SaveChangesAsync();
            typeId = type.Id;
        }

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new CreateCommonDataEntryCommand("Строка", typeId,
                JsonDocument.Parse("{}"), CatalogScope.System, null)));

        Assert.Contains("собственной таблице", refusal.Message);
    }

    // ── Вспомогательное ───────────────────────────────────────────────────────

    /// <summary>Схема с одним полем, ссылающимся на другой тип названным видом.</summary>
    private static JsonDocument Embedding(string key, string kind, Guid typeId) =>
        JsonDocument.Parse($$"""
            {"fields":[{"key":"{{key}}","title":"{{key}}","type":"{{kind}}","typeId":"{{typeId}}"}]}
            """);

    private async Task<DocumentType> TypeAsync(string code, string module,
        DocumentTypeKind kind = DocumentTypeKind.Composite)
        => await SendAsync(new CreateDocumentTypeCommand(
            code, code, kind, null, JsonDocument.Parse("""{"fields":[]}"""), module));

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var email = $"types_{Guid.NewGuid():N}@test.local";
        const string password = "Passw0rd!";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync("Admin"))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Админ", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
