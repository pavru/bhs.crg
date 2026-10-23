using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Подсказки поля внутри чужих записей (issue #1532): строки массивов и составные поля — по путям из
/// схем, частота по записям-владельцам, права, архив, <c>_baseRef</c>, порог, нормализация и отказ —
/// как у верхнего уровня, только тип в запросе — тип строки.
/// </summary>
public partial class FieldSuggestionTests
{
    private const string Name = "Наименование";

    [Fact]
    public async Task Строки_массивов_разных_записей_подсказываются_с_порогом_и_склейкой()
    {
        var (work, registry, _) = await RegistryAsync();
        var (b, _) = await SiteAsync("Журнал 1");
        var (c, _) = await SiteAsync("Журнал 2");
        await OwnerAsync(registry, CatalogScope.Construction, b, "Ёлка Пром", "Прокладка");
        await OwnerAsync(registry, CatalogScope.Construction, c, "елка  пром", "Монтаж");
        var (client, _) = await SignInAsync("Admin");

        // Близнецы из двух записей другой стройки — один пункт и проходят порог (написание — самой
        // свежей записи); одиночки — нет.
        Assert.Equal([new FieldSuggestion("елка  пром", false)], await ItemsAsync(client, $"type={work}&field={Name}&q=ел"));
        Assert.Empty(await ItemsAsync(client, $"type={work}&field={Name}&q=прок"));
        Assert.Empty(await ItemsAsync(client, $"type={work}&field={Name}&q=мон"));
        Assert.Empty(await ItemsAsync(client, $"type={work}&field={Name}&q=Елка пром"));
    }

    [Fact]
    public async Task Повтор_в_одном_журнале_весит_одну_запись()
    {
        var (work, registry, _) = await RegistryAsync();
        var (b, _) = await SiteAsync("Длинный журнал");
        var journal = await OwnerAsync(registry, CatalogScope.Construction, b, [.. Enumerable.Repeat("Кабель ВВГ", 50)]);
        var (client, _) = await SignInAsync("Admin");

        Assert.Empty(await ItemsAsync(client, $"type={work}&field={Name}"));
        using var scope = host.Services.CreateScope();
        var plan = Assert.IsType<SuggestionPlan.Read>(await PlanAsync(work));
        var rows = await scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>()
            .ReadAsync(plan.Types, Name, plan.Nested, SuggestionWindow.Default);
        // Пятьдесят строк — одна строка окна: значение записи-владельца, а не каждое вхождение.
        var row = Assert.Single(rows);
        Assert.Equal((journal, "Кабель ВВГ"), (row.RecordId, row.Value));
    }

    [Fact]
    public async Task Тот_же_ключ_другого_составного_типа_не_смешивается()
    {
        var (work, registry, org) = await RegistryAsync();
        await OwnerAsync(registry, CatalogScope.System, null, ["Монтаж"],
            customer: new Dictionary<string, object?> { [Name] = "ООО Мост" });
        var (client, _) = await SignInAsync("Admin");

        Assert.Equal(["Монтаж"], (await ItemsAsync(client, $"type={work}&field={Name}")).Select(i => i.Value));
        Assert.Equal(["ООО Мост"], (await ItemsAsync(client, $"type={org}&field={Name}")).Select(i => i.Value));
    }

    /// <summary>
    /// Массив в поле строки — порча данных, а не подсказка, как и у верхнего уровня: нестрогий jsonpath
    /// развернул бы его фильтром и отдал элементы.
    /// </summary>
    [Fact]
    public async Task Массив_в_поле_строки_не_подсказывается()
    {
        var (work, registry, _) = await RegistryAsync();
        await RecordAsync(registry, null, CatalogScope.System, null, extra: new()
        {
            ["Работы"] = new object[]
            {
                new Dictionary<string, object> { [Name] = new[] { "Альфа", "Бета" } },
                new Dictionary<string, object> { [Name] = "Ковка" },
            },
        });
        var (client, _) = await SignInAsync("Admin");

        Assert.Equal(["Ковка"], (await ItemsAsync(client, $"type={work}&field={Name}")).Select(i => i.Value));
    }

    /// <summary>
    /// Ссылка на запись справочника в составном поле — не источник: значение записи учтено верхним
    /// уровнем её типа. Посчитай ссылку — и одиночка с другой стройки прошла бы порог второй записью.
    /// </summary>
    [Fact]
    public async Task Ссылка_на_запись_справочника_дважды_не_считается()
    {
        var (_, registry, org) = await RegistryAsync();
        var (b, _) = await SiteAsync("Справочник");
        var (c, _) = await SiteAsync("Ссылка");
        var entry = await RecordAsync(org, null, CatalogScope.Construction, b, extra: new() { [Name] = "ООО Опора" });
        await OwnerAsync(registry, CatalogScope.Construction, c, ["Монтаж"], customer: new Dictionary<string, object?>
        {
            ["$ref"] = "catalog", ["entryId"] = entry.ToString(), [Name] = "ООО Опора",
        });
        var (client, _) = await SignInAsync("Admin");

        Assert.Empty(await ItemsAsync(client, $"type={org}&field={Name}"));
    }

    [Fact]
    public async Task Архив_и_наследованная_копия_строк_не_подсказывают()
    {
        var (work, registry, _) = await RegistryAsync();
        var only = await OwnerAsync(registry, CatalogScope.System, null, "Пайка");
        await RecordAsync(registry, null, CatalogScope.System, null, extra: new() { ["_baseRef"] = only.ToString() });
        var (client, _) = await SignInAsync("Admin");
        Assert.Equal(["Пайка"], (await ItemsAsync(client, $"type={work}&field={Name}")).Select(i => i.Value));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(only, archived: true);

        // Копия через _baseRef своих строк не имеет: с ушедшей в архив основой не подсказывает ничего.
        Assert.Empty(await ItemsAsync(client, $"type={work}&field={Name}"));
    }

    [Fact]
    public async Task Строки_документа_только_с_правом_на_документы()
    {
        var (work, _, _) = await RegistryAsync();
        var docType = await CreateTypeAsync(null, DocumentTypeKind.Document,
            $$"""{"fields":[{"key":"Работы","type":"array","typeId":"{{work}}"}]}""");
        var (_, sections) = await SiteAsync("Документ со строками", "Раздел");
        var set = (await SendAsync(new CreateDocumentSetCommand(sections[0], "Комплект"))).Id;
        var at = T0.AddMinutes(Interlocked.Increment(ref clock));
        await AddAsync(DomainObject.RestoreDocument(Guid.NewGuid(), docType, "Реестр",
            JsonSerializer.SerializeToDocument(new { Работы = new[] { new Dictionary<string, string> { [Name] = "Сварка" } } }),
            set, at, at, null, DocumentStatus.Draft, 0, null, null, null, JsonDocument.Parse("{}")));
        var query = $"type={work}&field={Name}&scope=Set&scopeId={set}";

        var (admin, _) = await SignInAsync("Admin");
        Assert.Equal(["Сварка"], (await ItemsAsync(admin, query)).Select(i => i.Value));
        Assert.Empty(await ItemsAsync(await ReaderAsync(CorePermissions.CatalogRead), query));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await ReaderAsync(CorePermissions.FilesUse)).GetAsync("/api/field-suggestions?" + query)).StatusCode);
    }

    /// <summary>
    /// Предел строк: окно обрывается на записи, где сумма строк превысила предел, — а самая свежая в нём
    /// всегда, даже одна сверх предела.
    /// </summary>
    [Fact]
    public async Task Окно_обрывается_на_пределе_строк_но_не_пустеет()
    {
        var (work, registry, _) = await RegistryAsync();
        await OwnerAsync(registry, CatalogScope.System, null, "Старая 1", "Старая 2", "Старая 3");
        await OwnerAsync(registry, CatalogScope.System, null, "Средняя 1", "Средняя 2", "Средняя 3");
        await OwnerAsync(registry, CatalogScope.System, null, "Свежая 1", "Свежая 2", "Свежая 3");
        var plan = Assert.IsType<SuggestionPlan.Read>(await PlanAsync(work));
        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>();

        async Task<string[]> Values(int lines) =>
            (await reader.ReadAsync(plan.Types, Name, plan.Nested, SuggestionWindow.Default with { MaxLines = lines }))
            .Select(r => r.Value).Order().ToArray();

        Assert.Equal(["Свежая 1", "Свежая 2", "Свежая 3", "Средняя 1", "Средняя 2", "Средняя 3"], await Values(6));
        Assert.Equal(["Свежая 1", "Свежая 2", "Свежая 3"], await Values(5));
        Assert.Equal(["Свежая 1", "Свежая 2", "Свежая 3"], await Values(1));
    }

    /// <summary>
    /// Тип и со своими записями, и в строках чужих: две части делят бюджет окна поровну — у каждой
    /// половина записей, а не полное окно (вдвое больше чтения под тем же пределом времени).
    /// </summary>
    [Fact]
    public async Task Верхний_уровень_и_строки_делят_окно_пополам()
    {
        var (work, registry, _) = await RegistryAsync();
        foreach (var i in new[] { 1, 2, 3 })
            await RecordAsync(work, null, CatalogScope.System, null, extra: new() { [Name] = $"Своя {i}" });
        foreach (var i in new[] { 1, 2, 3 }) await OwnerAsync(registry, CatalogScope.System, null, $"Строка {i}");
        var plan = Assert.IsType<SuggestionPlan.Read>(await PlanAsync(work));
        using var scope = host.Services.CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>()
            .ReadAsync(plan.Types, Name, plan.Nested, SuggestionWindow.Default with { MaxRecords = 4 });

        Assert.Equal(["Своя 2", "Своя 3", "Строка 2", "Строка 3"], rows.Select(r => r.Value).Order());
    }

    [Fact]
    public async Task Таймаут_строк_массивов_отвечает_503()
    {
        var (work, registry, _) = await RegistryAsync();
        await OwnerAsync(registry, CatalogScope.System, null, "Пусконаладка");
        var (client, _) = await SignInAsync("Admin");

        await using var blocker = await LockedAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await client.GetAsync($"/api/field-suggestions?type={work}&field={Name}", guard.Token);
        await blocker.DisposeAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ── Подготовка ─────────────────────────────────────────────────────────────

    /// <summary>Тип строки «Работа», составной «Организация» и тип-владелец с массивом и заказчиком.</summary>
    private async Task<(Guid Work, Guid Registry, Guid Org)> RegistryAsync()
    {
        const string named = $$"""{"fields":[{"key":"{{Name}}","type":"string"}]}""";
        var work = await CreateTypeAsync(null, DocumentTypeKind.Composite, named);
        var org = await CreateTypeAsync(null, DocumentTypeKind.Composite, named);
        var registry = await CreateTypeAsync(null, DocumentTypeKind.Composite, $$"""
            {"fields":[
              {"key":"Работы","type":"array","typeId":"{{work}}"},
              {"key":"Заказчик","type":"complex","typeId":"{{org}}"}
            ]}
            """);
        return (work, registry, org);
    }

    private Task<Guid> OwnerAsync(Guid registry, CatalogScope level, Guid? scopeId, params string[] works) =>
        OwnerAsync(registry, level, scopeId, works, customer: null);

    /// <summary>Запись-владелец: строки «Работы» с наименованиями и, если назван, заказчик.</summary>
    private Task<Guid> OwnerAsync(Guid registry, CatalogScope level, Guid? scopeId, string[] works,
        Dictionary<string, object?>? customer) =>
        RecordAsync(registry, null, level, scopeId, extra: new()
        {
            ["Работы"] = works.Select(w => new Dictionary<string, string> { [Name] = w }).ToArray(),
            ["Заказчик"] = customer,
        });

    private async Task<SuggestionPlan> PlanAsync(Guid type)
    {
        using var scope = host.Services.CreateScope();
        var types = await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>()
            .FindAsync(_ => true);
        return SuggestionSources.For(type, Name, types.ToDictionary(t => t.Id), TestAccess.All);
    }
}

