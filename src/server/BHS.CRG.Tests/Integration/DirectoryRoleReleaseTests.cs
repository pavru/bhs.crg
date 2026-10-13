using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using System.Net.Http.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Роль справочника не снимается с типа, пока на его записи ссылаются документы модулей
/// (решение владельца продукта от 13.10.2026, issue #1101).
///
/// <para>Счёт хранит идентификатор поставщика, а название читает по роли: снятая роль оставила бы
/// записи на месте, а счета — без поставщика. Тесты держат обе половины: отказ там, где ссылки есть,
/// и свободу там, где их нет, — правило, которое отказывает всегда, заперло бы роль навсегда.</para>
/// </summary>
public class DirectoryRoleReleaseTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task Роль_не_снимается_пока_на_организацию_ссылается_счёт()
    {
        var (client, _) = await SignInAsync("Admin");
        await CreateAsync(client);   // счёт с поставщиком из справочника организаций

        var organizations = await OrganizationsAsync();
        var without = Without(organizations.Schema, FunctionalTag.RoleOrganization);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new UpdateDocumentTypeSchemaCommand(organizations.Id, without)));

        Assert.Contains("«Справочник организаций»", refusal.Message);
        Assert.Contains("ссылаются документы модулей", refusal.Message);
        // Отказ — до записи: роль на месте, и счёт по-прежнему находит поставщика.
        Assert.True(SchemaTags.SchemaHasTypeTag((await OrganizationsAsync()).Schema, FunctionalTag.RoleOrganization));
    }

    /// <summary>Правка схемы, которая роль не трогает, проходит и при ссылках: стережём только снятие.</summary>
    [Fact]
    public async Task Правка_схемы_без_снятия_роли_проходит_при_живых_счетах()
    {
        var (client, _) = await SignInAsync("Admin");
        await CreateAsync(client);

        var organizations = await OrganizationsAsync();
        var same = JsonDocument.Parse(organizations.Schema.RootElement.GetRawText());

        var saved = await SendAsync(new UpdateDocumentTypeSchemaCommand(organizations.Id, same));

        Assert.True(SchemaTags.SchemaHasTypeTag(saved.Schema, FunctionalTag.RoleOrganization));
    }

    /// <summary>
    /// Справочник, на записи которого никто не ссылается, роль отпускает — и с записями, и без.
    /// Роль здесь своя (на один тест): у настоящих справочников общей базы ссылки есть всегда.
    /// </summary>
    [Fact]
    public async Task Роль_снимается_со_справочника_на_записи_которого_никто_не_ссылается()
    {
        var directory = new TestDirectory();
        var type = await SendAsync(new CreateDocumentTypeCommand(
            $"Свободный {Guid.NewGuid():N}", $"Свободный{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse(directory.BareSchema())));
        await SendAsync(new CreateCommonDataEntryCommand(
            "Запись без ссылок", type.Id, JsonDocument.Parse("{}"), CatalogScope.System, null));

        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetAllAsync();

        await DirectoryRoleRelease.EnsureAllowedAsync(
            directory.Tags,
            scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>(),
            scope.ServiceProvider.GetRequiredService<IRecordHolders>(),
            all.Single(t => t.Id == type.Id), JsonDocument.Parse("""{"fields":[]}"""), all, CancellationToken.None);
    }

    /// <summary>
    /// Та же проверка тем же вызовом ОТКАЗЫВАЕТ, когда ссылка есть, — иначе предыдущий тест был бы
    /// зелёным и у проверки, которая не срабатывает никогда.
    /// </summary>
    [Fact]
    public async Task Та_же_проверка_отказывает_справочнику_со_ссылкой()
    {
        var (client, _) = await SignInAsync("Admin");
        await CreateAsync(client);

        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetAllAsync();
        var organizations = all.Single(t => t.Code == TestDirectories.OrganizationCode);

        await Assert.ThrowsAsync<ConflictException>(() => DirectoryRoleRelease.EnsureAllowedAsync(
            scope.ServiceProvider.GetRequiredService<TagCatalog>(),
            scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>(),
            scope.ServiceProvider.GetRequiredService<IRecordHolders>(),
            organizations, Without(organizations.Schema, FunctionalTag.RoleOrganization), all, CancellationToken.None));
    }

    /// <summary>
    /// Справочники не делят записи (ревью PR #1318): подтип организаций номенклатурой не становится.
    /// Здесь — что проверка стоит на пути сохранения; её правило держит <c>RoleOverlapTests</c>.
    /// </summary>
    [Fact]
    public async Task Подтип_одного_справочника_не_становится_другим()
    {
        var organizations = await OrganizationsAsync();
        var mark = Guid.NewGuid().ToString("N")[..8];

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(new CreateDocumentTypeCommand(
            $"Поставщик-товар {mark}", $"ПоставщикТовар{mark}", DocumentTypeKind.Composite, organizations.Id,
            JsonDocument.Parse($$"""{"tags":["{{FunctionalTag.RoleNomenclature}}"],"fields":[]}"""))));

        Assert.Contains("вошли бы сразу в два справочника", refusal.Message);
    }

    /// <summary>
    /// Вторая дверь (ревью PR #1317): подтип, переставленный под другого родителя, уносит свои записи
    /// из справочника, хотя схему никто не трогал. Отказ — пока на запись ссылается счёт; подтип без
    /// ссылок уходит свободно — иначе правило заперло бы иерархию навсегда.
    /// </summary>
    [Fact]
    public async Task Подтип_не_выводится_из_справочника_пока_на_его_запись_ссылается_счёт()
    {
        var (client, _) = await SignInAsync("Admin");
        var organizations = await OrganizationsAsync();
        var mark = Guid.NewGuid().ToString("N")[..8];

        var held = await SendAsync(new CreateDocumentTypeCommand(
            $"Подтип со счётом {mark}", $"ПодтипСоСчётом{mark}", DocumentTypeKind.Composite, organizations.Id,
            JsonDocument.Parse("""{"fields":[]}""")));
        var record = await EntryAsync(held.Id, $"ООО «Подтип {mark}»");
        await OkAsync(await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = $"СЧ-{mark}", ["Дата"] = "2026-09-29", ["Поставщик"] = Reference(record),
            },
        }));

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new UpdateDocumentTypeCommand(held.Id, held.Name, held.Code, null)));

        Assert.Contains("нельзя вывести из справочника «Справочник организаций»", refusal.Message);
        Assert.Contains("ссылаются документы модулей", refusal.Message);

        // Переименование без смены родителя проверку не задевает.
        await SendAsync(new UpdateDocumentTypeCommand(held.Id, held.Name + " (переименован)", held.Code, organizations.Id));

        var free = await SendAsync(new CreateDocumentTypeCommand(
            $"Подтип без счетов {mark}", $"ПодтипБезСчетов{mark}", DocumentTypeKind.Composite, organizations.Id,
            JsonDocument.Parse("""{"fields":[]}""")));
        await EntryAsync(free.Id, $"ООО «Свободный {mark}»");

        var moved = await SendAsync(new UpdateDocumentTypeCommand(free.Id, free.Name, free.Code, null));
        Assert.Null(moved.ParentId);
    }

    /// <summary>Роль справочника документу не ставится: с ней приложение не нашло бы цель поля счёта.</summary>
    [Fact]
    public async Task Роль_справочника_документу_не_ставится()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(new CreateDocumentTypeCommand(
            $"Документ {mark}", $"Документ{mark}", DocumentTypeKind.Document, null,
            JsonDocument.Parse($$"""{"tags":["{{FunctionalTag.RoleNomenclature}}"],"fields":[]}"""))));

        Assert.Contains("справочником бывает только составной тип", refusal.Message);
    }

    private async Task<DocumentType> OrganizationsAsync()
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
            .FindAsync(t => t.Code == TestDirectories.OrganizationCode)).Single();
    }

    /// <summary>Та же схема без одного тэга типа — так её прислал бы редактор типов.</summary>
    private static JsonDocument Without(JsonDocument schema, string tag)
    {
        var root = (JsonObject)JsonNode.Parse(schema.RootElement.GetRawText())!;
        var kept = ((JsonArray)root["tags"]!).Select(t => t!.GetValue<string>()).Where(t => t != tag).ToList();
        root["tags"] = new JsonArray([.. kept.Select(t => (JsonNode?)t)]);
        return JsonDocument.Parse(root.ToJsonString());
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
