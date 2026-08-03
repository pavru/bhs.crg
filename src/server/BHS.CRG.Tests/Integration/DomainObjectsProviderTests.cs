using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системная консолидация «Общие данные: {тип}» (issue #627) — единственная с параметризованным
/// маркером. Колонки собираются по эффективной схеме типа, поэтому проверяются наследование,
/// исключения полей и столкновение ключа схемы с фиксированной колонкой.
/// </summary>
[Collection("Integration")]
public class DomainObjectsProviderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid SetId, Guid SectionId, Guid ConstructionId, Guid OrgTypeId, Guid ContractorTypeId);

    /// <summary>
    /// «Организация» и её подтип «Подрядчик»: подтип исключает поле предка и добавляет своё.
    /// Комплект живёт в разделе и стройке — чтобы проверить видимость по цепочке.
    /// </summary>
    private static async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var org = await m.Send(new CreateDocumentTypeCommand("Организация", "ORG", DocumentTypeKind.Composite, null,
            J("""
              {'fields':[
                {'key':'ИНН','type':'string','required':false},
                {'key':'Адрес','type':'string','required':false},
                {'key':'Контакты','type':'array','required':false},
                {'key':'ИмяОбъекта','type':'string','required':false}
              ]}
              """)));
        var contractor = await m.Send(new CreateDocumentTypeCommand("Подрядчик", "CONTR", DocumentTypeKind.Composite, org.Id,
            J("{'fields':[{'key':'СРО','type':'string','required':false}],'excludedFields':['Адрес']}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект 1"));
        return new Seed(set.Id, section.Id, construction.Id, org.Id, contractor.Id);
    }

    private static Task<Domain.Objects.DomainObject> AddEntryAsync(IServiceScope scope, Guid typeId, string name,
        string data, CatalogScope entryScope, Guid? scopeId, string[]? aliases = null)
        => M(scope).Send(new CreateCommonDataEntryCommand(name, typeId, J(data), entryScope, scopeId, aliases));

    private static string MarkerFor(Guid typeId) => $"{SystemDataSets.ObjectsMarkerPrefix}{typeId}";

    private static async Task<Guid> SourceAtAsync(IServiceScope scope, string level, Guid? scopeId, Guid typeId)
    {
        var svc = Svc(scope);
        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput(level, scopeId?.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Объекты", MarkerFor(typeId), null), default);
        return source.Id;
    }

    private static string? Cell(SourcePreviewDto p, int row, string column) =>
        p.Rows[row][p.Columns.ToList().IndexOf(column)];

    [Fact]
    public async Task Columns_FollowEffectiveSchema_AndFixedOnesWin()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.ContractorTypeId, "СтройМонтаж",
            "{'ИНН':'7701','СРО':'СРО-С-001','Адрес':'не должно быть колонки','ИмяОбъекта':'подмена'}",
            CatalogScope.System, null);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.ContractorTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var columns = preview!.Columns.ToList();

        Assert.Contains("ИНН", columns);          // унаследовано от предка
        Assert.Contains("СРО", columns);          // собственное поле подтипа
        Assert.DoesNotContain("Адрес", columns);  // исключено в подтипе
        Assert.DoesNotContain("Контакты", columns); // массив — не скалярная колонка

        // Ключ схемы «ИмяОбъекта» столкнулся с фиксированной колонкой: побеждает фиксированная,
        // иначе значение зависело бы от порядка сборки словаря.
        Assert.Equal(1, columns.Count(c => c == "ИмяОбъекта"));
        Assert.Equal("СтройМонтаж", Cell(preview, 0, "ИмяОбъекта"));
    }

    [Fact]
    public async Task Rows_CarryFixedColumnsAndScopeLabel()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "ЭнергоСтрой", "{'ИНН':'7702','Адрес':'Москва'}",
            CatalogScope.Section, seed.SectionId, ["ЭС", "Энерго"]);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Equal("1", Cell(preview!, 0, "НомерПП"));
        Assert.Equal("ЭнергоСтрой", Cell(preview, 0, "ИмяОбъекта"));
        Assert.Equal("ЭС, Энерго", Cell(preview, 0, "Алиасы"));
        Assert.Equal("ORG", Cell(preview, 0, "ТипКод"));
        Assert.Equal("Организация", Cell(preview, 0, "ТипИмя"));
        Assert.Equal("Раздел", Cell(preview, 0, "Уровень"));
        Assert.Equal("7702", Cell(preview, 0, "ИНН"));
        Assert.Equal("Москва", Cell(preview, 0, "Адрес"));
    }

    /// <summary>
    /// Записи подтипа — тоже записи типа (как у резолвера ссылок), а документы (Facet != null) в
    /// общие данные не попадают вовсе.
    /// </summary>
    [Fact]
    public async Task SubtypeEntries_AreIncluded_DocumentsAreNot()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "Организация", "{}", CatalogScope.System, null);
        await AddEntryAsync(scope, seed.ContractorTypeId, "Подрядчик", "{}", CatalogScope.System, null);

        // Документ того же типа: попасть в общие данные он не должен.
        var docType = await m.Send(new CreateDocumentTypeCommand("АОСР", "AOSR", DocumentTypeKind.Document, null,
            J("{'fields':[]}")));
        await m.Send(new AddDocumentToSetCommand(seed.SetId, docType.Id));

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Equal(2, preview!.Rows.Count);
        Assert.Equal(["Организация", "Подрядчик"],
            [.. preview.Rows.Select(r => r[preview.Columns.ToList().IndexOf("ИмяОбъекта")]).Order()]);
    }

    /// <summary>Видно то же, что в форме: цепочка вверх, но не соседний комплект.</summary>
    [Fact]
    public async Task Visibility_FollowsScopeChain()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var seed = await SeedAsync(scope);
        var other = await m.Send(new CreateDocumentSetCommand(seed.SectionId, "Комплект 2"));

        await AddEntryAsync(scope, seed.OrgTypeId, "Своя", "{}", CatalogScope.Set, seed.SetId);
        await AddEntryAsync(scope, seed.OrgTypeId, "Раздельная", "{}", CatalogScope.Section, seed.SectionId);
        await AddEntryAsync(scope, seed.OrgTypeId, "Общая", "{}", CatalogScope.System, null);
        await AddEntryAsync(scope, seed.OrgTypeId, "Чужая", "{}", CatalogScope.Set, other.Id);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var names = preview!.Rows.Select(r => r[preview.Columns.ToList().IndexOf("ИмяОбъекта")]).ToList();

        Assert.Equal(["Общая", "Раздельная", "Своя"], [.. names.Order()]);
        Assert.DoesNotContain("Чужая", names);
    }

    /// <summary>Одноимённые записи разных уровней НЕ схлопываются: их различает колонка «Уровень».</summary>
    [Fact]
    public async Task SameNameOnDifferentLevels_AreBothShown()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "Заказчик", "{'ИНН':'общий'}", CatalogScope.System, null);
        await AddEntryAsync(scope, seed.OrgTypeId, "Заказчик", "{'ИНН':'комплектный'}", CatalogScope.Set, seed.SetId);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Equal(2, preview!.Rows.Count);
        Assert.Equal(["Комплект", "Система"],
            [.. preview.Rows.Select(r => r[preview.Columns.ToList().IndexOf("Уровень")]).Order()]);
    }

    /// <summary>Наследник показывает унаследованные значения — то же, что видно в форме (issue #71).</summary>
    [Fact]
    public async Task BaseRef_IsMergedIntoRow()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var baseEntry = await AddEntryAsync(scope, seed.OrgTypeId, "Базовая", "{'ИНН':'7700','Адрес':'Москва'}",
            CatalogScope.System, null);
        await AddEntryAsync(scope, seed.OrgTypeId, "Наследник",
            $"{{'_baseRef':{{'kind':'catalog','id':'{baseEntry.Id}'}},'Адрес':'Тверь'}}",
            CatalogScope.System, null);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var heir = preview!.Rows.Single(r => r[preview.Columns.ToList().IndexOf("ИмяОбъекта")] == "Наследник");

        Assert.Equal("7700", heir[preview.Columns.ToList().IndexOf("ИНН")]);  // от базы
        Assert.Equal("Тверь", heir[preview.Columns.ToList().IndexOf("Адрес")]); // своё сильнее
    }

    /// <summary>
    /// Цепочка наследования разворачивается ЦЕЛИКОМ: внук показывает значение прадеда — ровно то,
    /// что напечатает документ. Один уровень оставлял бы у внука пустую ячейку.
    /// </summary>
    [Fact]
    public async Task BaseRefChain_IsResolvedToTheEnd()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var grand = await AddEntryAsync(scope, seed.OrgTypeId, "Прадед", "{'ИНН':'7700','Адрес':'Москва'}",
            CatalogScope.System, null);
        var parent = await AddEntryAsync(scope, seed.OrgTypeId, "Дед",
            $"{{'_baseRef':{{'kind':'catalog','id':'{grand.Id}'}},'Адрес':'Тверь'}}", CatalogScope.System, null);
        await AddEntryAsync(scope, seed.OrgTypeId, "Внук",
            $"{{'_baseRef':{{'kind':'catalog','id':'{parent.Id}'}}}}", CatalogScope.System, null);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var cols = preview!.Columns.ToList();
        var heir = preview.Rows.Single(r => r[cols.IndexOf("ИмяОбъекта")] == "Внук");

        Assert.Equal("7700", heir[cols.IndexOf("ИНН")]);   // от прадеда, через деда
        Assert.Equal("Тверь", heir[cols.IndexOf("Адрес")]); // дед переопределил
    }

    /// <summary>
    /// База вне видимого поддерева не наследуется — так же, как отказывается наследовать резолвер
    /// при выпуске. Иначе таблица показала бы значение, которого в документе не будет.
    /// </summary>
    [Fact]
    public async Task BaseRefOutsideScope_IsNotMerged()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var seed = await SeedAsync(scope);
        var other = await m.Send(new CreateDocumentSetCommand(seed.SectionId, "Комплект 2"));

        var foreign = await AddEntryAsync(scope, seed.OrgTypeId, "Чужая база", "{'ИНН':'9999'}",
            CatalogScope.Set, other.Id);
        await AddEntryAsync(scope, seed.OrgTypeId, "Наследник",
            $"{{'_baseRef':{{'kind':'catalog','id':'{foreign.Id}'}}}}", CatalogScope.System, null);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var cols = preview!.Columns.ToList();
        var heir = preview.Rows.Single(r => r[cols.IndexOf("ИмяОбъекта")] == "Наследник");

        Assert.True(string.IsNullOrEmpty(heir[cols.IndexOf("ИНН")]));
    }

    /// <summary>Данные живые: запись, добавленная после создания источника, попадает в строки.</summary>
    [Fact]
    public async Task RowsFollowCatalogState()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "Первая", "{}", CatalogScope.System, null);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId, seed.OrgTypeId);
        Assert.Single((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);

        await AddEntryAsync(scope, seed.OrgTypeId, "Вторая", "{}", CatalogScope.System, null);
        Assert.Equal(2, (await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows.Count);
    }

    /// <summary>
    /// Тип удалён вместе со своими записями, а источник остался: это ArgumentException, и список
    /// наборов не падает, а показывает запомненное число строк.
    /// </summary>
    [Fact]
    public async Task DeletedType_FailsSoftly_AndListSurvives()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "Единственная", "{}", CatalogScope.System, null);

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Объекты", MarkerFor(seed.OrgTypeId), null), default);
        Assert.Equal(1, source.CachedRowCount);

        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>()
            .Single(p => p.Handles(MarkerFor(seed.OrgTypeId)));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ProvideAsync(
            MarkerFor(Guid.NewGuid()), CatalogScope.Set, seed.SetId, default));

        // Маркер без идентификатора — тоже отказ, а не падение обходом.
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ProvideAsync(
            SystemDataSets.ObjectsMarkerPrefix + "не-guid", CatalogScope.Set, seed.SetId, default));

        var listed = Assert.Single(await svc.ListSourcesAsync(file.Id, default));
        Assert.Equal(source.Id, listed.Id);
    }

    /// <summary>Кандидат — на каждый тип с записями; типы без записей не предлагаются.</summary>
    [Fact]
    public async Task Candidates_OnlyForTypesWithEntries()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);

        Assert.DoesNotContain(await svc.ListSystemCandidatesAsync("Set", seed.SetId, default),
            c => c.SheetOrPath.StartsWith(SystemDataSets.ObjectsMarkerPrefix, StringComparison.Ordinal));

        await AddEntryAsync(scope, seed.ContractorTypeId, "СтройМонтаж", "{}", CatalogScope.System, null);

        var candidates = await svc.ListSystemCandidatesAsync("Set", seed.SetId, default);
        var objectCandidates = candidates
            .Where(c => c.SheetOrPath.StartsWith(SystemDataSets.ObjectsMarkerPrefix, StringComparison.Ordinal))
            .ToList();

        // Запись подтипа даёт кандидата на свой тип; у предка своих записей нет, но записи подтипа
        // принадлежат и ему — предлагать его отдельным кандидатом было бы дублем той же таблицы.
        var contractor = Assert.Single(objectCandidates);
        Assert.Equal($"{SystemDataSets.ObjectsMarkerPrefix}{seed.ContractorTypeId}", contractor.SheetOrPath);
        Assert.Equal("Общие данные: Подрядчик", contractor.Name);
        Assert.Equal(1, contractor.RowCount);
        Assert.Contains("СРО", contractor.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task RowCount_IsLiveInSourceList()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddEntryAsync(scope, seed.OrgTypeId, "Первая", "{}", CatalogScope.System, null);

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Объекты", MarkerFor(seed.OrgTypeId), null), default);
        Assert.Equal(1, source.CachedRowCount);

        await AddEntryAsync(scope, seed.OrgTypeId, "Вторая", "{}", CatalogScope.System, null);
        Assert.Equal(2, Assert.Single(await svc.ListSourcesAsync(file.Id, default)).CachedRowCount);
    }
}
