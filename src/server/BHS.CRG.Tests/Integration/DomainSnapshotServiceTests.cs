using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Снимок домена для внешнего агента (issue #419). Наборы данных отвечают «что в файлах», эти формы —
/// «что об этом знает система»; для сверки нужны оба источника.
/// </summary>
[Collection("Integration")]
public class DomainSnapshotServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IDomainSnapshotService Svc(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IDomainSnapshotService>();

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();

    private static string Code(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..12];

    /// <summary>Стройка → раздел → комплект → документ типа с одним полем.</summary>
    private async Task<(Guid constructionId, Guid setId, Guid docId, Guid typeId, IServiceScope scope)> SeedAsync()
    {
        var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var userId = Guid.NewGuid();

        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт освидетельствования", Code("AOSR"), DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[{"key":"НомерАкта","title":"Номер акта","type":"string"}]}""")));

        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        var doc = await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));

        await m.Send(new UpdateRequisitesCommand(doc.Id, JsonDocument.Parse("""{"НомерАкта":"12"}""")));
        return (construction.Id, set.Id, doc.Id, type.Id, scope);
    }

    [Fact]
    public async Task ListConstructions_IsEntryPoint_WithCounts()
    {
        var (constructionId, _, _, _, scope) = await SeedAsync();
        using (scope)
        {
            var list = await Svc(scope).ListConstructionsAsync(Guid.NewGuid());
            var c = Assert.Single(list.Items, x => x.Id == constructionId);
            Assert.Equal("ДНС Сити", c.Name);
            Assert.Equal(1, c.SectionCount);
            Assert.Equal(1, c.SetCount);
            Assert.Equal(1, c.DocumentCount);
        }
    }

    [Fact]
    public async Task GetConstruction_ReturnsSectionsAndSets()
    {
        var (constructionId, setId, _, _, scope) = await SeedAsync();
        using (scope)
        {
            var detail = await Svc(scope).GetConstructionAsync(constructionId);
            var section = Assert.Single(detail!.Sections);
            Assert.Equal("ЭОМ", section.Name);
            var set = Assert.Single(section.Sets, s => s.Id == setId);
            Assert.Equal(1, set.DocumentCount);
        }
    }

    [Fact]
    public async Task GetDocumentSet_CarriesContextOfSectionAndConstruction()
    {
        var (constructionId, setId, docId, _, scope) = await SeedAsync();
        using (scope)
        {
            // Контекст обязателен: имена документов между разделами повторяются, и находка без
            // «чей это комплект» непроверяема.
            var detail = await Svc(scope).GetDocumentSetAsync(setId);
            Assert.Equal("ЭОМ", detail!.SectionName);
            Assert.Equal(constructionId, detail.ConstructionId);
            Assert.Equal("ДНС Сити", detail.ConstructionName);

            var doc = Assert.Single(detail.Documents, d => d.Id == docId);
            Assert.Equal("Акт освидетельствования", doc.TypeName);
            Assert.False(string.IsNullOrEmpty(doc.Status));
        }
    }

    [Fact]
    public async Task GetDocument_ReturnsRequisites_AndPointsToItsType()
    {
        var (_, setId, docId, typeId, scope) = await SeedAsync();
        using (scope)
        {
            var doc = await Svc(scope).GetDocumentAsync(docId);
            Assert.Equal(typeId, doc!.TypeId);
            Assert.Equal(setId, doc.SetId);
            Assert.Equal("12", doc.Requisites.GetProperty("НомерАкта").GetString());

            // Схема того же типа — то, без чего ключи реквизитов внешнему читателю ничего не говорят.
            var schema = await Svc(scope).GetDocumentTypeAsync(doc.TypeId);
            var fields = schema!.Schema.GetProperty("fields");
            Assert.Contains(fields.EnumerateArray(), f => f.GetProperty("key").GetString() == "НомерАкта");
        }
    }

    /// <summary>
    /// Две формы реквизитов (issue #421): хранимая — точнее для сравнения тождества (entryId надёжнее
    /// строк имён), разрешённая — то, что попадает в PDF и что вообще читаемо человеком.
    /// </summary>
    [Fact]
    public async Task GetDocument_ResolvesCatalogRef_ButKeepsItRawOnRequest()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var orgType = await m.Send(new CreateDocumentTypeCommand(
            "Организация", Code("ORG"), DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Наименование","title":"Наименование","type":"string"}]}""")));
        var docType = await m.Send(new CreateDocumentTypeCommand(
            "Акт", Code("ACT"), DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[{"key":"Подрядчик","title":"Подрядчик","type":"complex"}]}""")));

        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));

        var org = await m.Send(new CreateCommonDataEntryCommand(
            "ООО Ромашка", orgType.Id,
            JsonDocument.Parse("""{"Наименование":"ООО Ромашка"}"""),
            CatalogScope.Construction, construction.Id));

        var doc = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));
        var refJson = $"{{\"Подрядчик\":{{\"$ref\":\"catalog\",\"scope\":\"Construction\",\"entryId\":\"{org.Id}\"}}}}";
        await m.Send(new UpdateRequisitesCommand(doc.Id, JsonDocument.Parse(refJson)));

        var svc = Svc(scope);

        var raw = await svc.GetDocumentAsync(doc.Id, resolveRefs: false);
        Assert.False(raw!.RefsResolved);
        var rawRef = raw.Requisites.GetProperty("Подрядчик");
        Assert.Equal("catalog", rawRef.GetProperty("$ref").GetString());
        Assert.Equal(org.Id.ToString(), rawRef.GetProperty("entryId").GetString());

        var resolved = await svc.GetDocumentAsync(doc.Id);
        Assert.True(resolved!.RefsResolved);
        var value = resolved.Requisites.GetProperty("Подрядчик");
        Assert.False(value.TryGetProperty("$ref", out _));
        Assert.Equal("ООО Ромашка", value.GetProperty("Наименование").GetString());
    }

    /// <summary>
    /// Табличное поле (issue #591). Реквизиты его значения не несут — строки подмешивает генерация, —
    /// и по прежнему ответу «таблицы нет» было неотличимо от «таблица придёт из набора»: внешний
    /// анализ принял реестр из 151 позиции за пустой и выдал ошибочное замечание.
    ///
    /// Заодно это единственная обратная ссылка «документ → источник»: из какой именно распознанной
    /// таблицы собран документ, больше узнать неоткуда.
    /// </summary>
    [Fact]
    public async Task GetDocument_ShowsTableFields_BoundAndUnbound()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var docType = await m.Send(new CreateDocumentTypeCommand(
            "Реестр материалов", Code("REG"), DocumentTypeKind.Document, null,
            JsonDocument.Parse("""
                {"fields":[
                    {"key":"Материалы","title":"Материалы","type":"array"},
                    {"key":"Приложения","title":"Приложения","type":"array"},
                    {"key":"НомерАкта","title":"Номер акта","type":"string"}
                ]}
                """)));

        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));
        var doc = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        // PDF-источник с кэшем строк: распознанная таблица, каких у кабельного журнала бывает три.
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var blobPath = await blob.UploadAsync("a.pdf", new MemoryStream([1, 2, 3]), "application/pdf");
        var rows = new[]
        {
            new Dictionary<string, string?> { ["Наименование"] = "Кабель ВВГнг 3х2.5" },
            new Dictionary<string, string?> { ["Наименование"] = "Лоток 100х50" },
        };
        var file = DataSetFile.Create("Альбом ЭОМ", DataSetFormat.Pdf, blobPath, CatalogScope.Construction, construction.Id);
        var source = file.AddSource("Таблица — Реестр материалов", "table:1",
            JsonSerializer.Serialize(new[] { new { name = "Наименование", sampleValues = new[] { "Кабель" } } }),
            rows.Length, null, JsonSerializer.Serialize(rows));
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        db.DataSetBindings.Add(DataSetBinding.For(doc.Id, source.Id, "Материалы", "{}"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var detail = await Svc(scope).GetDocumentAsync(doc.Id);

        var bound = Assert.Single(detail!.TableFields, f => f.Key == "Материалы");
        Assert.True(bound.BoundToDataset);
        Assert.Equal(source.Id, bound.SourceId);
        Assert.Equal(file.Id, bound.DatasetId);
        Assert.Equal("Таблица — Реестр материалов", bound.SourceName);
        Assert.Equal(2, bound.RowCount);

        // Непривязанное табличное поле — тоже ответ, причём тот, ради которого всё затевалось:
        // здесь «пусто» означает именно пусто.
        var unbound = Assert.Single(detail.TableFields, f => f.Key == "Приложения");
        Assert.False(unbound.BoundToDataset);
        Assert.Null(unbound.RowCount);

        // Скалярные поля в перечень не попадают — они и так видны в реквизитах.
        Assert.DoesNotContain(detail.TableFields, f => f.Key == "НомерАкта");
    }

    [Fact]
    public async Task CatalogEntries_AreReadableOnTheirOwn()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        var orgType = await m.Send(new CreateDocumentTypeCommand(
            "Организация", Code("ORG"), DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Наименование","title":"Наименование","type":"string"}]}""")));
        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var org = await m.Send(new CreateCommonDataEntryCommand(
            "ООО Ромашка", orgType.Id,
            JsonDocument.Parse("""{"Наименование":"ООО Ромашка"}"""),
            CatalogScope.Construction, construction.Id));

        var svc = Svc(scope);

        var entry = await svc.GetCatalogEntryAsync(org.Id);
        Assert.Equal("ООО Ромашка", entry!.Name);
        Assert.Equal("Construction", entry.Scope);
        Assert.Equal("Организация", entry.TypeName);

        Assert.Single((await svc.ListCatalogEntriesAsync("Construction", construction.Id, null, "ромашк")).Items,
            e => e.Id == org.Id);
        Assert.Empty((await svc.ListCatalogEntriesAsync(null, null, null, "не-существует")).Items);
    }

    /// <summary>
    /// ListCommonDataEntriesQuery по пути дёргает EnsureProfileAsync и СОЗДАЁТ объект-профиль уровня.
    /// Чтение через MCP писать в БД не имеет права — поэтому список каталога идёт мимо этого запроса.
    /// </summary>
    [Fact]
    public async Task ListCatalogEntries_DoesNotWriteToDatabase()
    {
        var (constructionId, _, _, _, scope) = await SeedAsync();
        using (scope)
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
            var before = (await repo.GetAllAsync()).Count;

            await Svc(scope).ListCatalogEntriesAsync("Construction", constructionId, null, null);

            Assert.Equal(before, (await repo.GetAllAsync()).Count);
        }
    }

    /// <summary>
    /// Карта связей должна повторять правило генерации: узкий уровень побеждает широкий. Разойдись
    /// она с QualityLinkResolver — агент видел бы не тот сертификат, что попадёт в документ.
    /// </summary>
    [Fact]
    public async Task MaterialQualityLinks_NarrowerScopeWins_AndCarriesProvenance()
    {
        var (constructionId, setId, _, _, scope) = await SeedAsync();
        using (scope)
        {
            var m = M(scope);
            var certType = await m.Send(new CreateDocumentTypeCommand(
                "Сертификат", Code("CERT"), DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}""")));

            async Task<Guid> CertAsync(string name) => (await m.Send(new CreateQualityDocumentCommand(
                certType.Id, name, JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null))).Id;

            var wide = await CertAsync("Сертификат общий");
            var narrow = await CertAsync("Сертификат комплекта");
            var only = await CertAsync("Сертификат стройки");

            // Один и тот же материал заведён и на System, и на комплекте — победить обязан комплект.
            await m.Send(new SetMaterialLinksCommand(CatalogScope.System, null, [new MaterialLinkInput("кабель-ввгнг-3х2.5")], wide));
            await m.Send(new SetMaterialLinksCommand(CatalogScope.Set, setId, [new MaterialLinkInput("кабель-ввгнг-3х2.5")], narrow));
            await m.Send(new SetMaterialLinksCommand(CatalogScope.Construction, constructionId, [new MaterialLinkInput("лоток-200")], only));

            var links = (await Svc(scope).ListMaterialQualityLinksAsync(setId)).Items;

            var cable = Assert.Single(links, l => l.MaterialKey == "кабель-ввгнг-3х2.5");
            Assert.Equal(narrow, cable.QualityDocumentId);
            Assert.Equal("Сертификат комплекта", cable.QualityDocumentName);
            Assert.Equal("Set", cable.Scope);
            Assert.Equal(setId, cable.ScopeId);

            // Уровень в ответе — не украшение: связь стройки действует на комплекте, и без провенанса
            // «почему тут этот сертификат» непроверяемо.
            var tray = Assert.Single(links, l => l.MaterialKey == "лоток-200");
            Assert.Equal("Construction", tray.Scope);
            Assert.Equal(constructionId, tray.ScopeId);
            Assert.Equal("Сертификат", tray.QualityDocumentTypeName);
        }
    }

    [Fact]
    public async Task MaterialQualityLinks_EmptyWhenNoneAndForMissingSet()
    {
        var (_, setId, _, _, scope) = await SeedAsync();
        using (scope)
        {
            Assert.Empty((await Svc(scope).ListMaterialQualityLinksAsync(setId)).Items);
            Assert.Empty((await Svc(scope).ListMaterialQualityLinksAsync(Guid.NewGuid())).Items);
        }
    }

    [Fact]
    public async Task Missing_ReturnsNull_NotThrows()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        Assert.Null(await svc.GetConstructionAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetDocumentSetAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetDocumentAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetDocumentTypeAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetCatalogEntryAsync(Guid.NewGuid()));
    }

    /// <summary>Документ — не запись каталога: иначе появился бы второй, менее полный путь к get_document.</summary>
    [Fact]
    public async Task GetCatalogEntry_RejectsDocument()
    {
        var (_, _, docId, _, scope) = await SeedAsync();
        using (scope) Assert.Null(await Svc(scope).GetCatalogEntryAsync(docId));
    }
}
