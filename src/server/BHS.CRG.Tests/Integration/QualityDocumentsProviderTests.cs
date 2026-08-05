using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системная консолидация «Документы качества» (issue #623): библиотека сертификатов, видимая с
/// уровня набора по цепочке вверх. Проверяется тот же сквозной путь, что у документов комплекта:
/// кандидат → источник → живые строки, — и видимость по уровням, ради которой всё и делается.
/// </summary>
[Collection("Integration")]
public class QualityDocumentsProviderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private sealed record Seed(Guid SetId, Guid SectionId, Guid ConstructionId, Guid CertTypeId, Guid LetterTypeId);

    /// <summary>
    /// Комплект в разделе и стройке + два подтипа документа качества с РАЗНЫМИ именами полей:
    /// провайдер обязан находить номер и срок по тэгам, а не по именам.
    /// </summary>
    private static async Task<Seed> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);

        var cert = await m.Send(new CreateDocumentTypeCommand("Сертификат", "CERT", DocumentTypeKind.Document, null,
            J("""
              {'fields':[
                {'key':'НомерДок','type':'string','required':false,'tags':['doc.number']},
                {'key':'ДатаВыдачи','type':'date','required':false,'tags':['doc.date']},
                {'key':'Действителен','type':'date','required':false,'tags':['quality.validUntil']},
                {'key':'Завод','type':'string','required':false,'tags':['quality.manufacturer']}
              ]}
              """)));

        // Информационное письмо: срока действия у него нет вовсе — тэга quality.validUntil в схеме нет.
        var letter = await m.Send(new CreateDocumentTypeCommand("Письмо", "LETTER", DocumentTypeKind.Document, null,
            J("{'fields':[{'key':'Рег','type':'string','required':false,'tags':['doc.number']}]}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект 1"));

        return new Seed(set.Id, section.Id, construction.Id, cert.Id, letter.Id);
    }

    private static Task<QualityDocument> AddDocAsync(
        IServiceScope scope, Guid typeId, string name, string requisites,
        CatalogScope docScope, Guid? scopeId, QualityDocSource source = QualityDocSource.Manual,
        string? scanBlobPath = null, string? scanFileName = null)
        => M(scope).Send(new CreateQualityDocumentCommand(
            typeId, name, J(requisites), docScope, scopeId, source, scanBlobPath, scanFileName, null));

    private static async Task<Guid> SourceAtAsync(IServiceScope scope, string level, Guid? scopeId)
    {
        var svc = Svc(scope);
        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput(level, scopeId?.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Качество", SystemDataSets.QualityDocumentsMarker, null), default);
        return source.Id;
    }

    [Fact]
    public async Task Candidate_BecomesSourceWithLiveRows()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);

        var cert = await AddDocAsync(scope, seed.CertTypeId, "EKF — автоматы",
            "{'НомерДок':'ЕАЭС RU С-CN.1','ДатаВыдачи':'2024-03-01','Действителен':'2029-02-28','Завод':'EKF'}",
            CatalogScope.Set, seed.SetId, QualityDocSource.Fgis, "blob/scan.pdf", "скан.pdf");

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var candidate = Assert.Single(await svc.DetectSourceCandidatesAsync(file.Id, default),
            c => c.SheetOrPath == SystemDataSets.QualityDocumentsMarker);
        Assert.Equal("Документы качества", candidate.Name);

        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Качество", candidate.SheetOrPath, null), default);
        var preview = await svc.PreviewSourceAsync(source.Id, 50, default);
        Assert.NotNull(preview);

        string? Cell(int row, string column) => preview.Rows[row][preview.Columns.ToList().IndexOf(column)];

        Assert.Single(preview.Rows);
        Assert.Equal("1", Cell(0, "НомерПП"));
        Assert.Equal(cert.Id.ToString(), Cell(0, "Ид"));
        Assert.Equal("EKF — автоматы", Cell(0, "Наименование"));
        Assert.Equal("CERT", Cell(0, "ТипКод"));
        Assert.Equal("Сертификат", Cell(0, "ТипИмя"));
        // Тэгированные поля найдены в чужой схеме: имена полей провайдеру неизвестны.
        Assert.Equal("ЕАЭС RU С-CN.1", Cell(0, "НомерДокумента"));
        Assert.Equal("2024-03-01", Cell(0, "ДатаДокумента"));
        Assert.Equal("2029-02-28", Cell(0, "СрокДействия"));
        Assert.Equal("EKF", Cell(0, "Изготовитель"));
        Assert.Equal("Комплект", Cell(0, "Уровень"));
        Assert.Equal("ФГИС", Cell(0, "Источник"));
        Assert.Equal("да", Cell(0, "ЕстьСкан"));
        Assert.Equal("скан.pdf", Cell(0, "ИмяФайлаСкана"));
    }

    /// <summary>Данные живые: документ, заведённый после создания источника, попадает в строки.</summary>
    [Fact]
    public async Task RowsFollowLibraryState()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.CertTypeId, "Первый", "{}", CatalogScope.Set, seed.SetId);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        Assert.Single((await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows);

        await AddDocAsync(scope, seed.CertTypeId, "Второй", "{}", CatalogScope.Set, seed.SetId);
        Assert.Equal(2, (await Svc(scope).PreviewSourceAsync(sourceId, 50, default))!.Rows.Count);
    }

    /// <summary>
    /// Ради чего консолидация и нужна: с комплекта видна вся цепочка вверх. Документ соседнего
    /// комплекта при этом не виден — иначе набор показывал бы одно, а генерация подставляла другое.
    /// </summary>
    [Fact]
    public async Task VisibilityFollowsScopeChainUpwards()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var seed = await SeedAsync(scope);
        var otherSet = await m.Send(new CreateDocumentSetCommand(seed.SectionId, "Комплект 2"));

        await AddDocAsync(scope, seed.CertTypeId, "Свой", "{}", CatalogScope.Set, seed.SetId);
        await AddDocAsync(scope, seed.CertTypeId, "Раздельный", "{}", CatalogScope.Section, seed.SectionId);
        await AddDocAsync(scope, seed.CertTypeId, "Строечный", "{}", CatalogScope.Construction, seed.ConstructionId);
        await AddDocAsync(scope, seed.CertTypeId, "Общий", "{}", CatalogScope.System, null);
        await AddDocAsync(scope, seed.CertTypeId, "Чужой", "{}", CatalogScope.Set, otherSet.Id);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var names = preview!.Rows.Select(r => r[preview.Columns.ToList().IndexOf("Наименование")]).ToList();

        Assert.Equal(["Общий", "Раздельный", "Свой", "Строечный"], [.. names.Order()]);
        Assert.DoesNotContain("Чужой", names);
    }

    /// <summary>Общесистемный документ виден и с уровня «Система», где объекта области нет вовсе.</summary>
    [Fact]
    public async Task SystemLevel_SeesOnlySystemWideDocuments()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.CertTypeId, "Общий", "{}", CatalogScope.System, null);
        await AddDocAsync(scope, seed.CertTypeId, "Комплектный", "{}", CatalogScope.Set, seed.SetId);

        var sourceId = await SourceAtAsync(scope, "System", null);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        Assert.Equal("Общий", Assert.Single(preview!.Rows)[preview.Columns.ToList().IndexOf("Наименование")]);
    }

    /// <summary>
    /// Тип без тэга «срок действия» даёт пустую ячейку, а не выпадает из выдачи: у информационного
    /// письма срока нет по природе, и молча прятать его было бы хуже пустой клетки.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutValidUntilTag_KeepsRowWithEmptyCell()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.LetterTypeId, "Письмо о продукции", "{'Рег':'исх-17'}",
            CatalogScope.Set, seed.SetId);

        var sourceId = await SourceAtAsync(scope, "Set", seed.SetId);
        var preview = await Svc(scope).PreviewSourceAsync(sourceId, 50, default);
        var row = Assert.Single(preview!.Rows);
        var columns = preview.Columns.ToList();

        Assert.Equal("исх-17", row[columns.IndexOf("НомерДокумента")]);
        Assert.True(string.IsNullOrEmpty(row[columns.IndexOf("СрокДействия")]));
        Assert.Equal("нет", row[columns.IndexOf("ЕстьСкан")]);
    }

    /// <summary>Число строк живое везде, где показывается: в списке источников и в MCP-срезе (#613).</summary>
    [Fact]
    public async Task RowCount_IsLiveEverywhereItIsShown()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.CertTypeId, "Первый", "{}", CatalogScope.Set, seed.SetId);

        var file = await svc.CreateSystemFileAsync(
            new CreateSystemFileInput("Set", seed.SetId.ToString(), null), default);
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Качество", SystemDataSets.QualityDocumentsMarker, null), default);
        Assert.Equal(1, source.CachedRowCount);

        await AddDocAsync(scope, seed.CertTypeId, "Второй", "{}", CatalogScope.System, null);

        Assert.Equal(2, Assert.Single(await svc.ListSourcesAsync(file.Id, default)).CachedRowCount);

        var snapshots = scope.ServiceProvider.GetRequiredService<IDataSnapshotService>();
        Assert.Equal(2, (await snapshots.GetSourceAsync(source.Id))!.RowCount);
        Assert.Equal(2, (await snapshots.GetRowsAsync(source.Id, 0, 50))!.TotalRows);
    }

    /// <summary>
    /// Уровень без объекта области (кроме «Системы») — отказ InvalidRequestException, и только он:
    /// NotFoundException из провайдера уронил бы весь список наборов, а не одну строку в нём.
    /// Отказ срабатывает уже при СОЗДАНИИ источника: число строк пишется в тот же момент.
    /// </summary>
    [Fact]
    public async Task LevelWithoutScopeId_IsRefusedAsInvalidRequest()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);
        await AddDocAsync(scope, seed.CertTypeId, "Общий", "{}", CatalogScope.System, null);

        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>()
            .Single(p => p.Handles(SystemDataSets.QualityDocumentsMarker));
        await Assert.ThrowsAsync<InvalidRequestException>(() => provider.ProvideAsync(
            SystemDataSets.QualityDocumentsMarker, CatalogScope.Section, null, default));

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Section", null, null), default);
        await Assert.ThrowsAsync<InvalidRequestException>(() => svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Качество", SystemDataSets.QualityDocumentsMarker, null), default));
    }

    /// <summary>
    /// А источник, СОХРАНЁННЫЙ на таком уровне (остался от версий до гейта #606 или уровень набора
    /// изменили), список наборов не роняет: счётчик ловит отказ и показывает запомненное число.
    /// </summary>
    [Fact]
    public async Task LegacySourceOnLevelWithoutScopeId_DoesNotBreakTheList()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        await SeedAsync(scope);

        var file = await svc.CreateSystemFileAsync(new CreateSystemFileInput("Section", null, null), default);

        // Мимо сервиса: сегодня такой источник создать нельзя — тем и ценен случай.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.DataSetFiles.FirstAsync(f => f.Id == file.Id);
        var source = entity.AddSource("Качество", SystemDataSets.QualityDocumentsMarker, "[]", 7);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();

        var listed = Assert.Single(await svc.ListSourcesAsync(file.Id, default));
        Assert.Equal(source.Id, listed.Id);
        Assert.Equal(7, listed.CachedRowCount); // запомненное число, а не падение
    }

    /// <summary>
    /// Пустая библиотека кандидата не даёт: иначе кнопка «Данные системы» появлялась бы и в
    /// установках, где модулем качества не пользуются.
    /// </summary>
    [Fact]
    public async Task NoDocuments_NoCandidate()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var seed = await SeedAsync(scope);

        Assert.DoesNotContain(await svc.ListSystemCandidatesAsync("Set", seed.SetId, default),
            c => c.SheetOrPath == SystemDataSets.QualityDocumentsMarker);

        await AddDocAsync(scope, seed.CertTypeId, "Появился", "{}", CatalogScope.System, null);

        Assert.Contains(await svc.ListSystemCandidatesAsync("Set", seed.SetId, default),
            c => c.SheetOrPath == SystemDataSets.QualityDocumentsMarker);
        // И на уровнях, где документов комплекта нет и быть не может.
        Assert.Contains(await svc.ListSystemCandidatesAsync("Construction", seed.ConstructionId, default),
            c => c.SheetOrPath == SystemDataSets.QualityDocumentsMarker);
        Assert.Contains(await svc.ListSystemCandidatesAsync("System", null, default),
            c => c.SheetOrPath == SystemDataSets.QualityDocumentsMarker);
    }
}
