using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системная консолидация «Материалы и документы качества» (issue #624): ПОБЕДИВШИЕ связки по
/// цепочке уровней — то, что подставится в документ при генерации. Ошибка здесь опаснее пустоты:
/// человек проверяет документ по этой таблице, и разойдись она с резолвером — расхождение видно
/// только по готовому PDF.
/// </summary>
[Collection("Integration")]
public class MaterialQualityProviderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid SetId, Guid SectionId, Guid ConstructionId, Guid CertTypeId);

    private static async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var cert = await m.Send(new CreateDocumentTypeCommand("Сертификат", "CERT", DocumentTypeKind.Document, null,
            J("""
              {'fields':[
                {'key':'НомерДок','type':'string','required':false,'tags':['doc.number']},
                {'key':'Действителен','type':'date','required':false,'tags':['quality.validUntil']},
                {'key':'Завод','type':'string','required':false,'tags':['quality.manufacturer']}
              ]}
              """)));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект 1"));
        return new Seed(set.Id, section.Id, construction.Id, cert.Id);
    }

    private static Task<QualityDocument> AddDocAsync(IServiceScope scope, Guid typeId, string name,
        string requisites = "{}", string? scanBlobPath = null)
        => M(scope).Send(new CreateQualityDocumentCommand(typeId, name, J(requisites),
            CatalogScope.System, null, QualityDocSource.Manual, scanBlobPath, null, null));

    private static Task<int> LinkAsync(IServiceScope scope, CatalogScope linkScope, Guid? scopeId,
        string key, Guid docId, string? label = null)
        => M(scope).Send(new SetMaterialLinksCommand(linkScope, scopeId, [new MaterialLinkInput(key, label)], docId));

    private static async Task<Guid> SourceAtAsync(IServiceScope scope, string level, Guid? scopeId)
    {
        var svc = Svc(scope);
        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput(level, scopeId?.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Связки", SystemDataSets.MaterialQualityMarker, null), default);
        return source.Id;
    }

    private static string? Cell(SourcePreviewDto p, int row, string column) =>
        p.Rows[row][p.Columns.ToList().IndexOf(column)];

    [Fact]
    public async Task Candidate_BecomesSourceWithLiveRows()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);

        var doc = await AddDocAsync(scope, seed.CertTypeId, "EKF — автоматы",
            "{'НомерДок':'ЕАЭС RU С-CN.1','Действителен':'2029-02-28','Завод':'EKF'}", "blob/scan.pdf");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, "выключатель av-125 | ekf", doc.Id,
            "Выключатель AV-125 EKF");

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var candidate = Assert.Single(await svc.DetectSourceCandidatesAsync(file.Id, default),
            c => c.SheetOrPath == SystemDataSets.MaterialQualityMarker);
        Assert.Equal("Материалы и документы качества", candidate.Name);

        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Связки", candidate.SheetOrPath, null), default);
        var preview = await svc.PreviewSourceAsync(source.Id, 50, default);
        Assert.NotNull(preview);
        Assert.Single(preview.Rows);

        Assert.Equal("1", Cell(preview, 0, "НомерПП"));
        Assert.Equal("Выключатель AV-125 EKF", Cell(preview, 0, "Материал"));
        Assert.Equal("выключатель av-125 | ekf", Cell(preview, 0, "КлючМатериала"));
        Assert.Equal(doc.Id.ToString(), Cell(preview, 0, "ИдДокумента"));
        Assert.Equal("EKF — автоматы", Cell(preview, 0, "ДокументНаименование"));
        Assert.Equal("Сертификат", Cell(preview, 0, "ТипИмя"));
        Assert.Equal("ЕАЭС RU С-CN.1", Cell(preview, 0, "НомерДокумента"));
        Assert.Equal("2029-02-28", Cell(preview, 0, "СрокДействия"));
        Assert.Equal("EKF", Cell(preview, 0, "Изготовитель"));
        Assert.Equal("Комплект", Cell(preview, 0, "УровеньСвязки"));
        Assert.Equal("да", Cell(preview, 0, "ЕстьСкан"));
    }

    /// <summary>
    /// Главное свойство: на один ключ побеждает связка УЗКОГО уровня — строка одна, и она та же,
    /// что подставится при генерации.
    /// </summary>
    [Fact]
    public async Task NarrowScopeWins_AndRowIsSingle()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var wide = await AddDocAsync(scope, seed.CertTypeId, "Общий сертификат");
        var narrow = await AddDocAsync(scope, seed.CertTypeId, "Сертификат комплекта");

        const string key = "кабель ввг | 3х2.5";
        await LinkAsync(scope, CatalogScope.Construction, seed.ConstructionId, key, wide.Id, "Кабель ВВГ 3х2.5");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, key, narrow.Id, "Кабель ВВГ 3х2.5");

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Single(preview!.Rows);
        Assert.Equal("Сертификат комплекта", Cell(preview, 0, "ДокументНаименование"));
        Assert.Equal("Комплект", Cell(preview, 0, "УровеньСвязки"));
    }

    /// <summary>А с уровня СТРОЙКИ виден широкий: узкой связки комплекта отсюда не существует.</summary>
    [Fact]
    public async Task WiderLevel_SeesItsOwnWinner()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var wide = await AddDocAsync(scope, seed.CertTypeId, "Общий сертификат");
        var narrow = await AddDocAsync(scope, seed.CertTypeId, "Сертификат комплекта");

        const string key = "кабель ввг | 3х2.5";
        await LinkAsync(scope, CatalogScope.Construction, seed.ConstructionId, key, wide.Id);
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, key, narrow.Id);

        var sourceId = await SourceAtAsync(scope, "Construction", seed.ConstructionId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);

        Assert.Single(preview!.Rows);
        Assert.Equal("Общий сертификат", Cell(preview, 0, "ДокументНаименование"));
        Assert.Equal("Стройка", Cell(preview, 0, "УровеньСвязки"));
    }

    /// <summary>
    /// Документ удалён — вместе с ним уходят и его связки (внешний ключ с каскадом, #554), поэтому
    /// строка не остаётся висеть. Ячейка «(документ удалён)» в провайдере всё же есть: её видно
    /// только у связки, пережившей ручную чистку базы, — тот же запас, что в списке связок.
    /// </summary>
    [Fact]
    public async Task DeletedDocument_TakesItsLinksAway()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Будет удалён");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, "материал | 1", doc.Id, "Материал 1");

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        Assert.Single((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);

        // Удаление — в отдельной области: команда чистит связки, а этот DbContext уже держит их
        // из привязки выше. Ограничение стенда, не поведения.
        using (var other = fixture.Services.CreateScope())
            await M(other).Send(new DeleteQualityDocumentCommand(doc.Id));

        Assert.Empty((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);
    }

    /// <summary>У связки без метки (заведена до #554) материал назван машинным ключом.</summary>
    [Fact]
    public async Task LinkWithoutLabel_ShowsKeyAsName()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Сертификат");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, "mb15-07-01m-54 | ", doc.Id);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        Assert.Equal("mb15-07-01m-54 | ", Cell(preview!, 0, "Материал"));
    }

    /// <summary>Данные живые: связка, заведённая после создания источника, попадает в строки.</summary>
    [Fact]
    public async Task RowsFollowLinkState()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Сертификат");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, "первый | ", doc.Id);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        Assert.Single((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);

        await LinkAsync(scope, CatalogScope.System, null, "второй | ", doc.Id);
        Assert.Equal(2, (await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows.Count);
    }

    [Fact]
    public async Task RowCount_IsLiveEverywhereItIsShown()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Сертификат");
        await LinkAsync(scope, CatalogScope.Set, seed.SetId, "первый | ", doc.Id);

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Связки", SystemDataSets.MaterialQualityMarker, null), default);
        Assert.Equal(1, source.CachedRowCount);

        await LinkAsync(scope, CatalogScope.System, null, "второй | ", doc.Id);

        Assert.Equal(2, Assert.Single(await svc.ListSourcesAsync(file.Id, default)).CachedRowCount);
        var snapshots = scope.ServiceProvider.GetRequiredService<IDataSnapshotService>();
        Assert.Equal(2, (await snapshots.GetSourceAsync(source.Id))!.RowCount);
    }

    [Fact]
    public async Task LevelWithoutScopeId_IsRefusedAsInvalidRequest()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Сертификат");
        await LinkAsync(scope, CatalogScope.System, null, "общий | ", doc.Id);

        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>()
            .Single(p => p.Handles(SystemDataSets.MaterialQualityMarker));
        await Assert.ThrowsAsync<InvalidRequestException>(() => provider.ProvideAsync(
            SystemDataSets.MaterialQualityMarker, CatalogScope.Section, null, default));
    }

    /// <summary>
    /// Место без комплекта (Guid.Empty) не получает даже общесистемных связок.
    ///
    /// Проверка резолва зовётся и для записи общих данных, а <c>DocumentView.From</c> кладёт в
    /// DocumentSetId «ScopeId ?? Guid.Empty». До выноса общей цепочки такой вызов упирался в
    /// «комплекта нет» и выходил; потеряй мы эту проверку — в объект без комплекта подмешались бы
    /// ВСЕ общесистемные сертификаты, а проверка отчиталась бы, что они разрешены.
    /// </summary>
    [Fact]
    public async Task PlaceWithoutSet_HasNoWinners()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Общий");
        await LinkAsync(scope, CatalogScope.System, null, "общий | ", doc.Id);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await MaterialQualityChain.WinnersAsync(db, CatalogScope.Set, Guid.Empty));
        Assert.Empty(await MaterialQualityChain.WinnersAsync(db, CatalogScope.Set, null));
        // А у самой системы связка есть — иначе проверка выше ничего бы не значила.
        Assert.Single(await MaterialQualityChain.WinnersAsync(db, CatalogScope.System, null));
    }

    /// <summary>Без связок кандидата нет — кнопку показывать незачем.</summary>
    [Fact]
    public async Task NoLinks_NoCandidate()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        var doc = await AddDocAsync(scope, seed.CertTypeId, "Сертификат без связок");

        Assert.DoesNotContain(await svc.ListSystemCandidatesAsync("Set", seed.SetId, default),
            c => c.SheetOrPath == SystemDataSets.MaterialQualityMarker);

        await LinkAsync(scope, CatalogScope.System, null, "появился | ", doc.Id);

        Assert.Contains(await svc.ListSystemCandidatesAsync("Set", seed.SetId, default),
            c => c.SheetOrPath == SystemDataSets.MaterialQualityMarker);
    }
}
