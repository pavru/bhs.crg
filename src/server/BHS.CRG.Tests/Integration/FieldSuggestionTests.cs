using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Адрес подсказок поля <c>GET /api/field-suggestions</c> (issue #1530, эпик #1527): своя стройка,
/// порог, архив, <c>_baseRef</c>, перекрытие ключа, права и отказы — на живой базе.
///
/// <para>База у класса общая с соседями хоста: у каждого теста свой тип, и значения не пересекаются.</para>
/// </summary>
[Collection("Integration")]
public partial class FieldSuggestionTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    private const string Maker = "Производитель";

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Своя_стройка_первой_чужие_от_двух_записей_система_своя()
    {
        var type = await FamilyAsync();
        var (a, aSections) = await SiteAsync("Своя", "Раздел");
        var aSet = (await SendAsync(new CreateDocumentSetCommand(aSections[0], "Комплект"))).Id;
        var (b, _) = await SiteAsync("Чужая");
        var (c, _) = await SiteAsync("Третья");

        await RecordAsync(type, "Альфа", CatalogScope.Construction, a);
        await RecordAsync(type, "Альбатрос", CatalogScope.Set, aSet);
        foreach (var site in new[] { b, b, b, c, c }) await RecordAsync(type, "Альтаир", CatalogScope.Construction, site);
        await RecordAsync(type, "Альма", CatalogScope.Construction, b);
        await RecordAsync(type, "Аляска", CatalogScope.System, null);

        var (client, _) = await SignInAsync("Admin");
        var items = await ItemsAsync(client, $"type={type}&field={Maker}&q=ал&scope=Construction&scopeId={a}");

        // Своя стройка — со всеми уровнями под ней; «вся система» — своя для любого (В-3).
        Assert.Equal(["Альбатрос", "Альфа", "Аляска"], items.Where(i => i.Here).Select(i => i.Value).Order());
        Assert.Equal([new FieldSuggestion("Альтаир", false)], items.Where(i => !i.Here));
        Assert.Equal(3, items.TakeWhile(i => i.Here).Count());

        // Тот же набор с комплекта своей стройки — то же «своё».
        var fromSet = await ItemsAsync(client, $"type={type}&field={Maker}&q=ал&scope=Set&scopeId={aSet}");
        Assert.Equal(items.OrderBy(i => i.Value), fromSet.OrderBy(i => i.Value));
    }

    [Fact]
    public async Task Ответ_на_проводе_только_значение_и_признак()
    {
        var type = await FamilyAsync();
        await RecordAsync(type, "Каппа", CatalogScope.System, null);
        var (client, _) = await SignInAsync("Admin");

        var response = await client.GetAsync($"/api/field-suggestions?type={type}&field={Maker}");
        await OkAsync(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["items"], body.EnumerateObject().Select(p => p.Name));
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(["here", "value"], item.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Неподсказываемое_поле_пусто_неизвестное_отказ()
    {
        var type = await FamilyAsync();
        await RecordAsync(type, "Лямбда", CatalogScope.System, null, extra: new()
        {
            ["ИНН"] = "7701234567",
            ["Комментарий"] = "Лямбда",
        });
        var (client, _) = await SignInAsync("Admin");

        Assert.Empty(await ItemsAsync(client, $"type={type}&field=ИНН"));
        Assert.Empty(await ItemsAsync(client, $"type={type}&field=Комментарий"));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/field-suggestions?type={type}&field=Цвет")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/field-suggestions?type={Guid.NewGuid()}&field={Maker}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/field-suggestions?type={type}&field={Maker}&scope=Construction")).StatusCode);

        // Отказы параметров — нашим текстом, а не пустым 400 привязки (ревью PR #1585); уровень — по
        // имени без учёта регистра, число — нет.
        foreach (var bad in new[] { "limit=abc", "limit=0", "limit=51", "scope=1", "scope=Section,Construction" })
        {
            var response = await client.GetAsync($"/api/field-suggestions?type={type}&field={Maker}&{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()), bad);
        }
        Assert.Empty(await ItemsAsync(client, $"type={type}&field=Комментарий&scope=system&limit=3"));
    }

    /// <summary>Живой тест строки «выбор» переписи мест чтения (ТЗ CORE-34.4).</summary>
    [Fact]
    public async Task Значение_архивной_записи_не_подсказывается()
    {
        var type = await FamilyAsync();
        var only = await RecordAsync(type, "Дельта", CatalogScope.System, null);
        var (client, _) = await SignInAsync("Admin");
        Assert.Equal(["Дельта"], (await ItemsAsync(client, $"type={type}&field={Maker}")).Select(i => i.Value));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(only, archived: true);

        Assert.Empty(await ItemsAsync(client, $"type={type}&field={Maker}"));
    }

    [Fact]
    public async Task Наследованная_копия_частоту_основы_не_поднимает()
    {
        var type = await FamilyAsync();
        var (b, _) = await SiteAsync("Основа");
        var (c, _) = await SiteAsync("Копия");
        var basis = await RecordAsync(type, "Омега", CatalogScope.Construction, b);
        await RecordAsync(type, null, CatalogScope.Construction, c, extra: new() { ["_baseRef"] = basis.ToString() });
        var (client, _) = await SignInAsync("Admin");

        Assert.Empty(await ItemsAsync(client, $"type={type}&field={Maker}"));

        await RecordAsync(type, "Омега", CatalogScope.Construction, c);
        Assert.Equal([new FieldSuggestion("Омега", false)], await ItemsAsync(client, $"type={type}&field={Maker}"));
    }

    [Fact]
    public async Task Потомок_перекрывший_ключ_выпадает_из_семьи_родителя()
    {
        var parent = await FamilyAsync();
        var child = await SubtypeAsync(parent, $$"""{"fields":[{"key":"{{Maker}}","type":"string"}]}""");
        var heir = await SubtypeAsync(parent, """{"fields":[]}""");
        await RecordAsync(child, "Сигма", CatalogScope.System, null);
        await RecordAsync(heir, "Ро", CatalogScope.System, null);
        var (client, _) = await SignInAsync("Admin");

        Assert.Equal(["Ро"], (await ItemsAsync(client, $"type={parent}&field={Maker}")).Select(i => i.Value));
        // Тип наследника ведёт к семье объявившего (В-2): «Ро» — его, «Сигма» — нет.
        Assert.Equal(["Ро"], (await ItemsAsync(client, $"type={heir}&field={Maker}")).Select(i => i.Value));
        Assert.Equal(["Сигма"], (await ItemsAsync(client, $"type={child}&field={Maker}")).Select(i => i.Value));
    }

    [Fact]
    public async Task Значения_документов_только_с_правом_на_документы_и_модулем()
    {
        var parent = await FamilyAsync();
        var docType = await SubtypeAsync(parent, """{"fields":[]}""", DocumentTypeKind.Document);
        var (_, sections) = await SiteAsync("Документы", "Раздел");
        var set = (await SendAsync(new CreateDocumentSetCommand(sections[0], "Комплект"))).Id;
        await DocumentAsync(docType, set, "Тау");
        await RecordAsync(parent, "Тэта", CatalogScope.System, null);
        var query = $"type={parent}&field={Maker}&scope=Set&scopeId={set}";

        var (admin, _) = await SignInAsync("Admin");
        Assert.Equal(["Тау", "Тэта"], (await ItemsAsync(admin, query)).Select(i => i.Value).Order());

        var catalogOnly = await ReaderAsync(CorePermissions.CatalogRead);
        Assert.Equal(["Тэта"], (await ItemsAsync(catalogOnly, query)).Select(i => i.Value));

        // Модуль документов выключен: документы не отдаются никому, общие данные — отдаются.
        var off = await SendAsync(new FieldSuggestionsQuery(parent, Maker, null, CatalogScope.Set, set, 8,
            TestAccess.WithoutModules(CorePermissions.CatalogRead, SuggestionSources.DocumentRead)));
        Assert.Equal(["Тэта"], off.Select(i => i.Value));
    }

    [Fact]
    public async Task Без_прав_на_все_источники_отказ()
    {
        var type = await FamilyAsync();
        await RecordAsync(type, "Ипсилон", CatalogScope.System, null);

        var stranger = await ReaderAsync(CorePermissions.FilesUse);
        var response = await stranger.GetAsync($"/api/field-suggestions?type={type}&field={Maker}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Справочник_номенклатуры_подсказывает_праву_новой_позиции()
    {
        var root = await TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        var kind = await SubtypeAsync(root, $$"""{"fields":[{"key":"{{Maker}}","type":"string"}]}""");
        await RecordAsync(kind, "Фи", CatalogScope.System, null);

        var intake = await ReaderAsync(SuggestionSources.NomenclatureEdit);

        Assert.Equal(["Фи"], (await ItemsAsync(intake, $"type={kind}&field={Maker}")).Select(i => i.Value));
    }

    /// <summary>
    /// Таймаут — 503, а не «пусто». Предел настоящий (300 мс): запрос упирается в замок таблицы,
    /// который держит соседняя транзакция, — ожидание замка предел времени тоже считает.
    /// </summary>
    [Fact]
    public async Task Таймаут_отвечает_503()
    {
        var type = await FamilyAsync();
        await RecordAsync(type, "Хи", CatalogScope.System, null);
        var (client, _) = await SignInAsync("Admin");

        await using var blocker = await LockedAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await client.GetAsync($"/api/field-suggestions?type={type}&field={Maker}", guard.Token);
        await blocker.DisposeAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("не успевают", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>
    /// Чтение внутри ЧУЖОЙ транзакции тоже под пределом времени, а отказ её не рушит: следующий запрос
    /// хозяина проходит, и предел у него — прежний (ревью PR #1585). Без точки сохранения отказ оставил
    /// бы транзакцию прерванной, а без предела чтение ждало бы замок, сколько тот держится.
    /// </summary>
    [Fact]
    public async Task В_чужой_транзакции_предел_есть_и_отказ_её_не_рушит()
    {
        var type = await FamilyAsync();
        await RecordAsync(type, "Пси", CatalogScope.System, null);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>();
        await using var outer = await db.Database.BeginTransactionAsync();
        var before = await StatementTimeoutAsync(db);

        await using (await LockedAsync())
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<SuggestionsTimedOutException>(
                () => reader.ReadAsync([type], Maker, [], SuggestionWindow.Default, guard.Token));
        }

        Assert.Equal(before, await StatementTimeoutAsync(db));
        Assert.Equal("Пси", Assert.Single(await reader.ReadAsync([type], Maker, [], SuggestionWindow.Default)).Value);
        Assert.Equal(before, await StatementTimeoutAsync(db));
    }

    private static Task<string> StatementTimeoutAsync(AppDbContext db) =>
        db.Database.SqlQueryRaw<string>("SELECT current_setting('statement_timeout') AS \"Value\"").SingleAsync();

    /// <summary>
    /// Таблица записей под исключительным замком соседней транзакции: запрос к ней ждёт, и предел времени
    /// ожидание замка тоже считает. Замок снимается освобождением.
    /// </summary>
    private async Task<IAsyncDisposable> LockedAsync()
    {
        var blocker = new NpgsqlConnection(host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"));
        await blocker.OpenAsync();
        var tx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE domain_objects IN ACCESS EXCLUSIVE MODE", blocker, tx))
            await lockCmd.ExecuteNonQueryAsync();
        return blocker;
    }

    // ── Подготовка ─────────────────────────────────────────────────────────────

    private static string Mark() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Свой тип семьи: подсказываемое поле, ИНН и выключенное вручную.</summary>
    private Task<Guid> FamilyAsync() => CreateTypeAsync(null, DocumentTypeKind.Composite, $$"""
        {"fields":[
          {"key":"{{Maker}}","type":"string"},
          {"key":"ИНН","type":"string","tags":["org.inn"]},
          {"key":"Комментарий","type":"string","tags":["input.noSuggest"]}
        ]}
        """);

    private Task<Guid> SubtypeAsync(Guid parent, string schema, DocumentTypeKind kind = DocumentTypeKind.Composite) =>
        CreateTypeAsync(parent, kind, schema);

    private async Task<Guid> CreateTypeAsync(Guid? parent, DocumentTypeKind kind, string schema)
    {
        var mark = Mark();
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var created = DocumentType.Create($"Подсказки {mark}", $"Подсказки{mark}", kind, parent,
            JsonDocument.Parse(schema), TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(created);
        await types.SaveChangesAsync();
        return created.Id;
    }

    private static int clock;

    /// <summary>Запись со значением поля; время правки растёт от записи к записи — порядок свежести известен.</summary>
    private async Task<Guid> RecordAsync(Guid type, string? maker, CatalogScope level, Guid? scopeId,
        Dictionary<string, object?>? extra = null)
    {
        var data = new Dictionary<string, object?>(extra ?? []);
        if (maker is not null) data[Maker] = maker;
        var at = T0.AddMinutes(Interlocked.Increment(ref clock));
        var record = DomainObject.Restore(Guid.NewGuid(), type, maker ?? "без имени",
            JsonSerializer.SerializeToDocument(data), level, scopeId, at, at, null, archivedAt: null);
        await AddAsync(record);
        return record.Id;
    }

    private async Task DocumentAsync(Guid type, Guid set, string maker)
    {
        var at = T0.AddMinutes(Interlocked.Increment(ref clock));
        await AddAsync(DomainObject.RestoreDocument(Guid.NewGuid(), type, maker,
            JsonSerializer.SerializeToDocument(new Dictionary<string, string> { [Maker] = maker }),
            set, at, at, null, DocumentStatus.Draft, 0, null, null, null, JsonDocument.Parse("{}")));
    }

    private async Task AddAsync(DomainObject record)
    {
        using var scope = host.Services.CreateScope();
        var objects = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        await objects.AddAsync(record);
        await objects.SaveChangesAsync();
    }

    private static async Task<List<FieldSuggestion>> ItemsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/field-suggestions?" + query);
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(i => new FieldSuggestion(i.GetProperty("value").GetString()!, i.GetProperty("here").GetBoolean()))
            .ToList();
    }

    /// <summary>Читатель с ролью ровно из названных прав: у системных ролей такого состава нет.</summary>
    private async Task<HttpClient> ReaderAsync(params string[] permissions)
    {
        var (admin, _) = await SignInAsync("Admin");
        var created = await admin.PostAsJsonAsync("/api/roles", new
        {
            title = $"Подсказки {Mark()}",
            summary = "Проверка прав подсказок",
            permissions,
        });
        await OkAsync(created);
        var role = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        return (await SignInAsync(role)).Client;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
