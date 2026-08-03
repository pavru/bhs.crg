using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системная консолидация «Документы раздела/стройки» (issue #626): реестр уровнем выше комплекта.
/// Отдельно от «Документов комплекта» — колонок больше, и адрес документа в поддереве (комплект,
/// раздел) здесь и есть главное, ради чего консолидация нужна.
/// </summary>
[Collection("Integration")]
public class SubtreeDocumentsProviderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid ConstructionId, Guid SectionA, Guid SectionB, Guid SetA1, Guid SetA2, Guid SetB1, Guid TypeId);

    /// <summary>
    /// Стройка с двумя разделами: «АВ» (два комплекта) и «ЭОМ» (один). Имена разделов нарочно
    /// таковы, что алфавитный порядок не совпадает с порядком создания.
    /// </summary>
    private static async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var type = await m.Send(new CreateDocumentTypeCommand("АОСР", "AOSR", DocumentTypeKind.Document, null,
            J("""
              {'fields':[
                {'key':'Номер','type':'string','required':false,'tags':['doc.number']},
                {'key':'Листов','type':'number','required':false,'tags':['doc.pageCount']}
              ]}
              """)));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var eom = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var av = await m.Send(new CreateSectionCommand(construction.Id, "АВ"));

        var eom1 = await m.Send(new CreateDocumentSetCommand(eom.Id, "ЭОМ-1"));
        var av2 = await m.Send(new CreateDocumentSetCommand(av.Id, "АВ-2"));
        var av1 = await m.Send(new CreateDocumentSetCommand(av.Id, "АВ-1"));

        return new Seed(construction.Id, av.Id, eom.Id, av1.Id, av2.Id, eom1.Id, type.Id);
    }

    private static async Task<Guid> AddDocAsync(IServiceScope scope, Guid setId, Guid typeId, string name, string? number = null)
    {
        var m = M(scope);
        var doc = await m.Send(new AddDocumentToSetCommand(setId, typeId));
        await m.Send(new RenameDocumentInstanceCommand(doc.Id, name));
        if (number is not null)
            await m.Send(new UpdateRequisitesCommand(doc.Id, J($"{{'Номер':'{number}'}}")));
        return doc.Id;
    }

    private static async Task<Guid> SourceAtAsync(IServiceScope scope, string level, Guid scopeId)
    {
        var svc = Svc(scope);
        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput(level, scopeId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Реестр", SystemDataSets.SubtreeDocumentsMarker, null), default);
        return source.Id;
    }

    private static string? Cell(SourcePreviewDto p, int row, string column) =>
        p.Rows[row][p.Columns.ToList().IndexOf(column)];

    [Fact]
    public async Task Section_CollectsDocumentsOfAllItsSets()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1", "И-1");
        await AddDocAsync(scope, seed.SetA2, seed.TypeId, "АОСР 2", "И-2");
        await AddDocAsync(scope, seed.SetB1, seed.TypeId, "Чужой раздел", "И-3");

        var sourceId = await SourceAtAsync(scope, "Section", seed.SectionA);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Equal(2, preview!.Rows.Count);
        Assert.Equal("АОСР 1", Cell(preview, 0, "Наименование"));
        Assert.Equal("АВ-1", Cell(preview, 0, "Комплект"));
        Assert.Equal("АВ", Cell(preview, 0, "Раздел"));
        Assert.Equal(seed.SetA1.ToString(), Cell(preview, 0, "ИдКомплекта"));
        Assert.Equal(seed.SectionA.ToString(), Cell(preview, 0, "ИдРаздела"));
        Assert.Equal("И-1", Cell(preview, 0, "НомерДокумента"));
        Assert.Equal("АОСР 2", Cell(preview, 1, "Наименование"));
        Assert.Equal("АВ-2", Cell(preview, 1, "Комплект"));
    }

    /// <summary>
    /// Порядок реестра: раздел → комплект → место в комплекте; НомерПП сквозной, а
    /// ПорядокВКомплекте — внутри своего комплекта и потому повторяется.
    /// </summary>
    [Fact]
    public async Task Construction_SortsBySectionThenSetThenOrder()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetB1, seed.TypeId, "ЭОМ первый");   // раздел «ЭОМ» — по алфавиту второй
        await AddDocAsync(scope, seed.SetA2, seed.TypeId, "АВ-2 первый");
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АВ-1 первый");
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АВ-1 второй");

        var sourceId = await SourceAtAsync(scope, "Construction", seed.ConstructionId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Equal(
            ["АВ-1 первый", "АВ-1 второй", "АВ-2 первый", "ЭОМ первый"],
            [.. preview!.Rows.Select(r => r[preview.Columns.ToList().IndexOf("Наименование")])]);
        Assert.Equal(["1", "2", "3", "4"],
            [.. preview.Rows.Select(r => r[preview.Columns.ToList().IndexOf("НомерПП")])]);
        // Внутри комплекта нумерация своя: у первых документов обоих комплектов «АВ» она одна и та же.
        Assert.Equal("0", Cell(preview, 0, "ПорядокВКомплекте"));
        Assert.Equal("1", Cell(preview, 1, "ПорядокВКомплекте"));
        Assert.Equal("0", Cell(preview, 2, "ПорядокВКомплекте"));
    }

    /// <summary>
    /// Метаданные сборки известны только у собранных комплектов — об этом источник предупреждает.
    /// Молчаливые пустые ячейки «КоличествоЛистов» читались бы как «листов нет».
    /// </summary>
    [Fact]
    public async Task UnassembledSets_AreReportedAsWarning()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");
        await AddDocAsync(scope, seed.SetA2, seed.TypeId, "АОСР 2");

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Section", seed.SectionA.ToString(), null), default);
        var candidate = Assert.Single(await svc.DetectSourceCandidatesAsync(file.Id, default),
            c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);

        Assert.Equal("Документы раздела", candidate.Name);
        Assert.NotNull(candidate.Warning);
        Assert.Contains("Не собрано документов: 2 из 2", candidate.Warning);

        // И у самого источника — живой, считается вместе со строками.
        await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Реестр", candidate.SheetOrPath, null), default);
        var listed = Assert.Single(await svc.ListSourcesAsync(file.Id, default));
        Assert.Contains("Не собрано документов", listed.Warning);
    }

    [Fact]
    public async Task Construction_NamesCandidateAfterItsLevel()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");

        var candidate = Assert.Single(
            await svc.ListSystemCandidatesAsync("Construction", seed.ConstructionId, default),
            c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);
        Assert.Equal("Документы стройки", candidate.Name);
    }

    /// <summary>
    /// На комплекте реестр поддерева не предлагается: там «Документы комплекта», и два похожих
    /// кандидата рядом заставляли бы выбирать по догадке.
    /// </summary>
    [Fact]
    public async Task SetLevel_OffersSetDocumentsOnly()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");

        var candidates = await svc.ListSystemCandidatesAsync("Set", seed.SetA1, default);
        Assert.Contains(candidates, c => c.SheetOrPath == SystemDataSets.SetDocumentsMarker);
        Assert.DoesNotContain(candidates, c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);
    }

    [Fact]
    public async Task SystemLevel_IsRefusedWithArgumentException()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");

        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>()
            .Single(p => p.Handles(SystemDataSets.SubtreeDocumentsMarker));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ProvideAsync(
            SystemDataSets.SubtreeDocumentsMarker, CatalogScope.System, null, default));

        // И на уровне «Система» такой кандидат не предлагается — предложить было бы нечего.
        Assert.DoesNotContain(await Svc(scope).ListSystemCandidatesAsync("System", null, default),
            c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);
    }

    /// <summary>Данные живые: комплект, добавленный в раздел после создания источника, попадает в реестр.</summary>
    [Fact]
    public async Task RowsFollowSubtreeState()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");

        var sourceId = await SourceAtAsync(scope, "Section", seed.SectionA);
        Assert.Single((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);

        var fresh = await m.Send(new CreateDocumentSetCommand(seed.SectionA, "АВ-3"));
        await AddDocAsync(scope, fresh.Id, seed.TypeId, "АОСР 3");

        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        Assert.Equal(2, preview!.Rows.Count);
        Assert.Equal("АВ-3", Cell(preview, 1, "Комплект"));
    }

    [Fact]
    public async Task RowCount_IsLiveInSourceList()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "АОСР 1");

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Section", seed.SectionA.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Реестр", SystemDataSets.SubtreeDocumentsMarker, null), default);
        Assert.Equal(1, source.CachedRowCount);

        await AddDocAsync(scope, seed.SetA2, seed.TypeId, "АОСР 2");
        Assert.Equal(2, Assert.Single(await svc.ListSourcesAsync(file.Id, default)).CachedRowCount);
    }

    /// <summary>
    /// Пустой раздел кандидата НЕ отменяет: реестр настраивают заранее, до ввода документов, а
    /// кнопка «Данные системы» появляется только при непустом списке кандидатов — спрятать
    /// предложение значило бы спрятать саму возможность. «Документы комплекта» ведут себя так же.
    /// </summary>
    [Fact]
    public async Task EmptySubtree_StillOffersCandidate()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var candidate = Assert.Single(
            await Svc(scope).ListSystemCandidatesAsync("Section", seed.SectionA, default),
            c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);
        Assert.Equal(0, candidate.RowCount);
        Assert.Null(candidate.Warning); // строк нет — и оговаривать нечего
    }

    /// <summary>
    /// Оговорка считается по ДОКУМЕНТАМ: один документ, собранный вручную, не делает «собранным»
    /// весь комплект — иначе она замолкала бы там, где соседние строки стоят с пустыми ячейками.
    /// </summary>
    [Fact]
    public async Task PartlyGeneratedSet_StillWarnsAboutTheRest()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var first = await AddDocAsync(scope, seed.SetA1, seed.TypeId, "Собран вручную");
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "Черновик 1");
        await AddDocAsync(scope, seed.SetA1, seed.TypeId, "Черновик 2");

        // Статус «Сгенерирован» ставит одиночная генерация (DomainObject.AddGeneratedFile), сборки
        // комплекта при этом не было. Здесь ставим его напрямую: выпускать документ по-настоящему
        // ради статуса значило бы тащить в тест шаблон, Typst и хранилище.
        using (var other = fixture.Services.CreateScope())
        {
            var db = other.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE document_facets SET "Status" = 'Generated' WHERE "ObjectId" = {first}""");
        }

        var candidate = Assert.Single(
            await Svc(scope).ListSystemCandidatesAsync("Section", seed.SectionA, default),
            c => c.SheetOrPath == SystemDataSets.SubtreeDocumentsMarker);
        Assert.Contains("Не собрано документов: 2 из 3", candidate.Warning);
    }
}
