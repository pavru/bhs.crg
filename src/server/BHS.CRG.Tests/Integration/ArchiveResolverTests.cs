using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Архив и всё, что превращает строку в запись (ТЗ CORE-34.4, issue #1185, шаг 4): резолвер, привязки
/// наборов данных, сверка связок, а с ними чтения, которые отдают записи машине, — системный набор
/// объектов и MCP.
///
/// <para>Общее правило одно: архивная запись НАХОДИТСЯ и называется архивной. Спрятать её значило
/// бы ответить «не найдено» — и человек завёл бы дубль; подставить молча — значило бы вернуть в
/// новые данные запись, которую из выбора убрали.</para>
///
/// <para>Каждый шаг — в своей области служб: резолвер держит индекс кандидатов всё время жизни
/// области, и признак, поставленный после первого обращения, он бы уже не увидел.</para>
/// </summary>
[Collection("Integration")]
public class ArchiveResolverTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private const string OrgSchema = "{'fields':[{'key':'ИНН','type':'string','tags':['identity']}]}";

    private async Task<T> InScopeAsync<T>(Func<IServiceScope, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();
        return await work(scope);
    }

    private Task<Guid> TypeAsync(string code, string schema, DocumentTypeKind kind = DocumentTypeKind.Composite) =>
        InScopeAsync(async s => (await M(s).Send(new CreateDocumentTypeCommand(code, code, kind, null, J(schema)))).Id);

    private Task<Guid> EntryAsync(Guid typeId, string name, string data,
        CatalogScope scope = CatalogScope.System, Guid? scopeId = null) =>
        InScopeAsync(async s => (await M(s).Send(new CreateCommonDataEntryCommand(name, typeId, J(data), scope, scopeId))).Id);

    private Task ArchiveAsync(Guid id, bool archived = true) => InScopeAsync(async s =>
    {
        Assert.Equal(ArchiveOutcome.Changed,
            await s.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived));
        return 0;
    });

    private Task<Guid> SetAsync() => InScopeAsync(async s =>
    {
        var m = M(s);
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "Раздел"));
        return (await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"))).Id;
    });

    private Task<ObjectMatch?> ResolveAsync(ObjectMatchRequest req, CatalogScope scope = CatalogScope.System, Guid? scopeId = null) =>
        InScopeAsync(s => s.ServiceProvider.GetRequiredService<IObjectResolver>().ResolveAsync(req, scope, scopeId));

    // ── Резолвер ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Архивная_запись_находится_и_названа_архивной()
    {
        var type = await TypeAsync("ORG_A", OrgSchema);
        var id = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}");
        await ArchiveAsync(id);

        var identity = new Dictionary<string, string?> { ["ИНН"] = "7701" };
        Assert.Equal(new ObjectMatch(id, Archived: true), await ResolveAsync(ObjectMatchRequest.ByName(type, "ромашка")));
        Assert.Equal(new ObjectMatch(id, Archived: true), await ResolveAsync(ObjectMatchRequest.ByIdentity(type, identity)));
        Assert.Equal(new ObjectMatch(id, Archived: true), await ResolveAsync(ObjectMatchRequest.ByField(type, "ИНН", "7701")));

        // Вернули — снова действующая: признак читается из базы, а не помнится.
        await ArchiveAsync(id, archived: false);
        Assert.Equal(new ObjectMatch(id, Archived: false), await ResolveAsync(ObjectMatchRequest.ByName(type, "ромашка")));
    }

    /// <summary>
    /// Без этого порядка узкий уровень побеждал бы всегда, и архивная запись комплекта заслонила бы
    /// действующую системную: строка, у которой есть законная цель, осталась бы без ссылки.
    /// </summary>
    [Fact]
    public async Task Действующая_системная_запись_побеждает_архивную_запись_комплекта()
    {
        var set = await SetAsync();
        var type = await TypeAsync("ORG_B", OrgSchema);
        var live = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}");
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'7701'}", CatalogScope.Set, set);

        // До архива — узкий уровень, как и было.
        Assert.Equal(archived, (await ResolveAsync(ObjectMatchRequest.ByName(type, "Ромашка"), CatalogScope.Set, set))!.Value.Id);

        await ArchiveAsync(archived);
        var identity = new Dictionary<string, string?> { ["ИНН"] = "7701" };
        foreach (var req in new[]
                 {
                     ObjectMatchRequest.ByName(type, "Ромашка"),
                     ObjectMatchRequest.ByIdentity(type, identity),
                     ObjectMatchRequest.ByField(type, "ИНН", "7701"),
                 })
            Assert.Equal(new ObjectMatch(live, Archived: false), await ResolveAsync(req, CatalogScope.Set, set));
    }

    [Fact]
    public async Task Пакетный_ответ_несёт_признак_архива()
    {
        var type = await TypeAsync("ORG_C", OrgSchema);
        var live = await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'2'}");
        await ArchiveAsync(archived);

        var res = await InScopeAsync(s => M(s).Send(new ResolveObjectsBatchQuery(CatalogScope.System, null,
        [
            new(type, ObjectMatchStrategy.Name, "Лютик"),
            new(type, ObjectMatchStrategy.Name, "Ромашка"),
        ])));

        Assert.Equal((live, false), (res[0]!.EntryId, res[0]!.Archived));
        Assert.Equal((archived, true), (res[1]!.EntryId, res[1]!.Archived));
        Assert.Equal("Ромашка", res[1]!.DisplayName);
    }

    /// <summary>
    /// Тот же ответ, но каким его видит экран. Адрес собирает ответ СВОЕЙ формой, и признак,
    /// дошедший до обработчика, наружу однажды не доехал: вставка таблицы подставила бы архивную
    /// запись как обычную. Поймано на стенде, а не тестом — отсюда этот тест.
    /// </summary>
    [Fact]
    public async Task Адрес_пакетного_резолвера_отдаёт_признак_архива()
    {
        var type = await TypeAsync("ORG_H", OrgSchema);
        await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        await ArchiveAsync(await EntryAsync(type, "Ромашка", "{'ИНН':'2'}"));

        var client = await SignInAsync();
        var response = await client.PostAsJsonAsync("/api/objects/resolve-batch", new
        {
            scope = "System",
            items = new[]
            {
                new { typeId = type, strategy = "Name", value = "Лютик" },
                new { typeId = type, strategy = "Name", value = "Ромашка" },
            },
        });
        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(found[0].GetProperty("archived").GetBoolean());
        Assert.True(found[1].GetProperty("archived").GetBoolean());
    }

    private async Task<HttpClient> SignInAsync()
    {
        var email = $"arch_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Arch";
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

    // ── Привязка набора данных ────────────────────────────────────────────────

    private sealed record Bound(Guid OrgType, Guid Org, Guid Owner);

    /// <summary>
    /// Запись «Договор» с привязкой набора: поле «Подрядчик» заполняется ссылкой на организацию,
    /// найденную по названию из колонки источника.
    /// </summary>
    private async Task<Bound> BoundRecordAsync(string ownerData = "{}")
    {
        var orgType = await TypeAsync("ORG_D", OrgSchema);
        var org = await EntryAsync(orgType, "Ромашка", "{'ИНН':'7701'}");
        var ownerType = await TypeAsync("DOG_D", $"{{'fields':[{{'key':'Подрядчик','type':'object','typeId':'{orgType}'}}]}}");
        var owner = await EntryAsync(ownerType, "Договор 1", ownerData.Replace("{org}", org.ToString()));
        await BindAsync(owner, orgType);
        return new Bound(orgType, org, owner);
    }

    private Task BindAsync(Guid owner, Guid orgType) => InScopeAsync(async s =>
    {
        var svc = Svc(s);
        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes("Орг\nРомашка\n"), "orgs.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Организации", candidate.SheetOrPath, null), TestAccess.All, default);
        await svc.CreateBindingAsync(new CreateBindingInput(owner, source.Id, null, new Dictionary<string, string>
        {
            ["Подрядчик"] = $$"""@@ref:{"strategy":"Name","column":"Орг","typeId":"{{orgType}}"}""",
        }), default);
        return 0;
    });

    private Task<JsonElement> SaveAsync(Guid owner, string data = "{}") => InScopeAsync(async s =>
        (await M(s).Send(new UpdateCommonDataEntryCommand(owner, "Договор 1", J(data), TestAccess.All))).Data.RootElement.Clone());

    private Task<BindingCheckItem> CheckAsync(Guid owner) => InScopeAsync(async s =>
        Assert.Single((await M(s).Send(new CheckCommonDataBindingsQuery(owner, TestAccess.All))).Items));

    private static string? RefOf(JsonElement data) =>
        data.TryGetProperty("Подрядчик", out var v) && v.ValueKind == JsonValueKind.Object ? v.GetProperty("entryId").GetString() : null;

    /// <summary>
    /// Сохранение записи кладёт ссылку в её данные — это выбор, сделанный машиной. Архивную запись
    /// в новые данные он не возвращает, и сверка связок говорит об этом «в архиве», а не «не найдено».
    /// </summary>
    [Fact]
    public async Task Сохранение_не_ставит_новую_ссылку_на_архивную_запись()
    {
        var b = await BoundRecordAsync();
        await ArchiveAsync(b.Org);

        Assert.Null(RefOf(await SaveAsync(b.Owner)));

        var item = await CheckAsync(b.Owner);
        // Свой статус, а не общий «в архиве»: поле не заполняется, и это есть что чинить.
        Assert.Equal("archived-skipped", item.Status);
        Assert.Contains("в архиве", item.Detail);
        Assert.Contains("не подставлена", item.Detail);

        // Вернули из архива — привязка заработала без единой правки.
        await ArchiveAsync(b.Org, archived: false);
        Assert.Equal(b.Org.ToString(), RefOf(await SaveAsync(b.Owner)));
        Assert.Equal("matched", (await CheckAsync(b.Owner)).Status);
    }

    /// <summary>
    /// Ссылка, стоявшая до архива, сохранение переживает: иначе правка любого другого поля договора
    /// молча стёрла бы подрядчика, ушедшего в архив.
    /// </summary>
    [Fact]
    public async Task Стоявшая_ссылка_на_архивную_запись_сохранение_переживает()
    {
        var b = await BoundRecordAsync();
        Assert.Equal(b.Org.ToString(), RefOf(await SaveAsync(b.Owner)));
        await ArchiveAsync(b.Org);

        // Форма прислала данные без поля вовсе — привязка обязана вернуть его, как возвращала всегда.
        Assert.Equal(b.Org.ToString(), RefOf(await SaveAsync(b.Owner)));

        var item = await CheckAsync(b.Owner);
        Assert.Equal("archived", item.Status);
        Assert.Equal("Ромашка", item.LinkedName);
        Assert.Contains("связка сохранена", item.Detail);
    }

    /// <summary>
    /// «Стояла» решают сохранённые данные, а не тело запроса: иначе архивную цель достаточно было
    /// бы прислать с формой — и правило обходилось бы любым клиентом.
    ///
    /// <para>С правилом записи ядра (шаг 5) такое сохранение не «проходит без подстановки», а
    /// отвергается целиком: присланная ссылка — новая, и её останавливает охрана записи.</para>
    /// </summary>
    [Fact]
    public async Task Стоявшей_ссылку_делают_сохранённые_данные_а_не_присланные()
    {
        var b = await BoundRecordAsync();
        await ArchiveAsync(b.Org);

        var sent = $"{{'Примечание':{{'$ref':'catalog','entryId':'{b.Org}'}}}}";
        var refusal = await Assert.ThrowsAsync<BHS.CRG.Application.Schema.RecordWriteRefusedException>(
            () => SaveAsync(b.Owner, sent));
        Assert.Equal(BHS.CRG.Application.Schema.ArchivedRefRule.ArchivedRef, Assert.Single(refusal.Details).Code);
    }

    /// <summary>
    /// У документа снимка нет: значение собирается заново при каждой генерации, и это чтение.
    /// Архивный подрядчик в документе остаётся — иначе перегенерация документа закрытого периода
    /// потеряла бы его, — но с предупреждением.
    /// </summary>
    [Fact]
    public async Task Генерация_подставляет_архивную_запись_и_предупреждает()
    {
        var orgType = await TypeAsync("ORG_E", OrgSchema);
        var org = await EntryAsync(orgType, "Ромашка", "{'ИНН':'7701'}");
        var docType = await TypeAsync("AKT_E", $"{{'fields':[{{'key':'Подрядчик','type':'object','typeId':'{orgType}'}}]}}",
            DocumentTypeKind.Document);
        var set = await SetAsync();
        var doc = await InScopeAsync(async s => (await M(s).Send(new AddDocumentToSetCommand(set, docType))).Id);
        await BindAsync(doc, orgType);
        await ArchiveAsync(org);

        var (data, diagnostics) = await InScopeAsync(async s =>
        {
            var view = DocumentView.From((await M(s).Send(new GetDocumentInstanceQuery(doc)))!);
            var ctx = await s.ServiceProvider.GetRequiredService<IEntityResolver>().ResolveAsync(view);
            var found = new List<ResolutionDiagnostic>();
            await s.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, TestAccess.All, found, default);
            return (JsonSerializer.SerializeToElement(ctx.Data), found);
        });

        Assert.Equal(org.ToString(), RefOf(data));
        var warning = Assert.Single(diagnostics);
        Assert.Equal((DiagnosticSeverity.Warning, ArchivedRefCodes.Kept), (warning.Severity, warning.Code));
        Assert.Contains("печатается как прежде", warning.Message);
    }

    /// <summary>
    /// Таблица, где архивный поставщик стоит в каждой строке, даёт ОДНО предупреждение: триста
    /// одинаковых закрыли бы собой настоящие.
    /// </summary>
    [Fact]
    public async Task Генерация_предупреждает_об_архивной_записи_один_раз_на_значение()
    {
        var orgType = await TypeAsync("ORG_I", OrgSchema);
        var org = await EntryAsync(orgType, "Ромашка", "{'ИНН':'7701'}");
        var rowType = await TypeAsync("ROW_I", $"{{'fields':[{{'key':'Поставщик','type':'object','typeId':'{orgType}'}}]}}");
        var docType = await TypeAsync("REG_I", "{'fields':[{'key':'Строки','type':'array'}]}", DocumentTypeKind.Document);
        var set = await SetAsync();
        var doc = await InScopeAsync(async s => (await M(s).Send(new AddDocumentToSetCommand(set, docType))).Id);
        await InScopeAsync(async s =>
        {
            var svc = Svc(s);
            var file = await svc.UploadFileAsync(new UploadFileInput(
                Encoding.UTF8.GetBytes("Орг\nРомашка\nРомашка\nРомашка\n"), "rows.csv", "text/csv", "Тест", "System", null), default);
            var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single();
            var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Строки", candidate.SheetOrPath, null), TestAccess.All, default);
            await svc.SetMaterializationAsync(source.Id, rowType, new Dictionary<string, string>
            {
                ["Поставщик"] = $$"""@@ref:{"strategy":"Name","column":"Орг","typeId":"{{orgType}}"}""",
            }, discriminator: null, byIdColumn: null, default);
            await svc.CreateBindingAsync(new CreateBindingInput(doc, source.Id, "Строки", null), default);
            return 0;
        });
        await ArchiveAsync(org);

        var (rows, diagnostics) = await InScopeAsync(async s =>
        {
            var view = DocumentView.From((await M(s).Send(new GetDocumentInstanceQuery(doc)))!);
            var ctx = await s.ServiceProvider.GetRequiredService<IEntityResolver>().ResolveAsync(view);
            var found = new List<ResolutionDiagnostic>();
            await s.ServiceProvider.GetRequiredService<IDataSetResolver>().InjectAsync(ctx, view, TestAccess.All, found, default);
            return (JsonSerializer.SerializeToElement(ctx.Data).GetProperty("Строки"), found);
        });

        Assert.Equal(3, rows.GetArrayLength());
        Assert.All(rows.EnumerateArray(), r => Assert.Equal(org.ToString(), r.GetProperty("Поставщик").GetProperty("entryId").GetString()));
        Assert.Single(diagnostics, d => d.Code == ArchivedRefCodes.Kept);
    }

    // ── Чтения, отдающие записи машине ────────────────────────────────────────

    /// <summary>
    /// Системный набор объектов архивные записи не прячет — реестр уже выпущенного документа
    /// изменился бы при перегенерации, — а называет колонкой.
    /// </summary>
    [Fact]
    public async Task Системный_набор_объектов_называет_архивные_колонкой()
    {
        var type = await TypeAsync("ORG_F", OrgSchema);
        await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        await ArchiveAsync(await EntryAsync(type, "Ромашка", "{'ИНН':'2'}"));

        var preview = await InScopeAsync(async s =>
        {
            var svc = Svc(s);
            var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("System", null, null), default);
            var source = await svc.CreateSourceAsync(file.Id,
                new CreateSourceInput("Объекты", $"{SystemDataSets.ObjectsMarkerPrefix}{type}", null), TestAccess.All, default);
            return (await svc.PreviewSourceAsync(source.Id, 50, TestAccess.All, default))!;
        });

        var columns = preview.Columns.ToList();
        var byName = preview.Rows.ToDictionary(
            r => r[columns.IndexOf("ИмяОбъекта")]!, r => r[columns.IndexOf("ВАрхиве")]);
        Assert.Equal("нет", byName["Лютик"]);
        Assert.Equal("да", byName["Ромашка"]);
    }

    /// <summary>Агент предлагает значения человеку — без признака он предложил бы запись, которую выбрать нельзя.</summary>
    [Fact]
    public async Task Mcp_отдаёт_признак_архива_в_списке_и_в_записи()
    {
        var type = await TypeAsync("ORG_G", OrgSchema);
        var live = await EntryAsync(type, "Лютик", "{'ИНН':'1'}");
        var archived = await EntryAsync(type, "Ромашка", "{'ИНН':'2'}");
        await ArchiveAsync(archived);

        await InScopeAsync(async s =>
        {
            var snapshots = s.ServiceProvider.GetRequiredService<IDomainSnapshotService>();
            var listed = (await snapshots.ListCatalogEntriesAsync(null, null, type, null)).Items.ToDictionary(e => e.Id, e => e.Archived);
            Assert.False(listed[live]);
            Assert.True(listed[archived]);
            Assert.True((await snapshots.GetCatalogEntryAsync(archived))!.Archived);
            Assert.False((await snapshots.GetCatalogEntryAsync(live))!.Archived);
            return 0;
        });
    }
}
