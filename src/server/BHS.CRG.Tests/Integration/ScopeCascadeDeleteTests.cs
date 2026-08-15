using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Maintenance;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каскадное удаление уровня расположения (issue #739).
///
/// <para>Две беды, обе от одного: у объекта нет внешнего ключа на комплект — ось расположения
/// полиморфна. База уносит разделы за стройкой и комплекты за разделом, а объекты оставляет, и
/// прикладной каскад был только у комплекта: удаление раздела или стройки плодило сирот. Оно же
/// обходило с фланга поштучные guard'ы (#71/#269/#735) — объекты уходили пачкой, и ссылки на них
/// молча повисали.</para>
///
/// <para>Ключевая граница проверяется отдельно: держатель ВНУТРИ удаляемого поддерева блокировать
/// не должен. Иначе комплект с двумя связанными документами стал бы неудаляемым навсегда.</para>
/// </summary>
[Collection("Integration")]
public class ScopeCascadeDeleteTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private readonly Guid _userId = Guid.NewGuid();

    private sealed record Seed(Guid ConstructionId, Guid SectionId, Guid SetId, Guid DocTypeId);

    private async Task<Seed> SeedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var construction = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));
        var docType = await m.Send(new CreateDocumentTypeCommand(
            "Акт", "AOSR", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        return new Seed(construction.Id, section.Id, set.Id, docType.Id);
    }

    private async Task<Guid> AddDocumentAsync(Seed seed, string name, string requisites = "{}")
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.DocTypeId));
        await M(scope).Send(new RenameDocumentInstanceCommand(inst.Id, name));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        return inst.Id;
    }

    private async Task<int> CountObjectsAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        return (await repo.FindAsync(_ => true)).Count;
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = fixture.Services.CreateScope();
        await M(scope).Send(request);
    }

    // ── Сироты: каскад обязан уносить объекты поддерева ──────────────────────────

    [Fact]
    public async Task DeletingSection_RemovesObjectsOfItsSets()
    {
        var seed = await SeedAsync();
        await AddDocumentAsync(seed, "Акт 1");
        await AddDocumentAsync(seed, "Акт 2");
        Assert.Equal(2, await CountObjectsAsync());

        await SendAsync(new DeleteSectionCommand(seed.SectionId));

        // Комплект уносит база (FK раздела), объекты — прикладной каскад. Не унеси он их —
        // остались бы сироты: в интерфейсе невидимы, в базе живы.
        Assert.Equal(0, await CountObjectsAsync());
    }

    [Fact]
    public async Task DeletingConstruction_RemovesObjectsOfAllLevels()
    {
        var seed = await SeedAsync();
        await AddDocumentAsync(seed, "Акт");
        using (var scope = fixture.Services.CreateScope())
        {
            // Общие данные на каждом уровне поддерева — их тоже уносит только прикладной каскад.
            await M(scope).Send(new CreateCommonDataEntryCommand(
                "Данные раздела", seed.DocTypeId, J("{}"), CatalogScope.Section, seed.SectionId));
            await M(scope).Send(new CreateCommonDataEntryCommand(
                "Данные стройки", seed.DocTypeId, J("{}"), CatalogScope.Construction, seed.ConstructionId));
        }
        Assert.Equal(3, await CountObjectsAsync());

        await SendAsync(new DeleteConstructionCommand(seed.ConstructionId));

        Assert.Equal(0, await CountObjectsAsync());
    }

    /// <summary>Объекты чужой стройки каскад не трогает — поддерево, а не «всё подряд».</summary>
    [Fact]
    public async Task DeletingConstruction_LeavesOtherConstructionsAlone()
    {
        var seed = await SeedAsync();
        await AddDocumentAsync(seed, "Акт своей стройки");
        Guid otherSetId;
        using (var scope = fixture.Services.CreateScope())
        {
            var other = await M(scope).Send(new CreateConstructionCommand("Чужой объект", _userId));
            var section = await M(scope).Send(new CreateSectionCommand(other.Id, "СС"));
            var set = await M(scope).Send(new CreateDocumentSetCommand(section.Id, "СС-1"));
            otherSetId = set.Id;
            await M(scope).Send(new AddDocumentToSetCommand(otherSetId, seed.DocTypeId));
        }

        await SendAsync(new DeleteConstructionCommand(seed.ConstructionId));

        Assert.Equal(1, await CountObjectsAsync());
    }

    // ── Guard: ссылки извне ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingSet_ReferencedFromQualityLibrary_IsRejected()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "Акт", "{'Номер':'7'}");
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new CreateQualityDocumentCommand(
                seed.DocTypeId, "Протокол испытаний",
                J($"{{'Основание':{{'$ref':'document','instanceId':'{docId}','fieldKey':'Номер'}}}}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteDocumentSetCommand(seed.SetId)));

        Assert.Contains("документ качества «Протокол испытаний»", ex.Message);
        Assert.Equal(1, await CountObjectsAsync()); // ничего не снесено
    }

    /// <summary>Тот же отказ на верхних уровнях: правило одно на комплект, раздел и стройку.</summary>
    [Fact]
    public async Task DeletingConstruction_ReferencedFromOutside_IsRejected()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "Акт", "{'Номер':'7'}");
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new CreateQualityDocumentCommand(
                seed.DocTypeId, "Сертификат",
                J($"{{'Основание':{{'$ref':'document','instanceId':'{docId}','fieldKey':'Номер'}}}}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteConstructionCommand(seed.ConstructionId)));
        await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteSectionCommand(seed.SectionId)));
    }

    /// <summary>
    /// Граница guard'а и главное, что он НЕ должен ломать. Документ, ссылающийся на соседний
    /// документ того же комплекта, уходит вместе с ним: ссылка исчезает целиком, а не повисает.
    /// Считай мы и такого держателя — комплект с двумя связанными документами стал бы неудаляемым
    /// навсегда, распутать это можно было бы только правкой реквизитов вручную.
    /// </summary>
    [Fact]
    public async Task DeletingSet_WithReferencesInsideItself_Succeeds()
    {
        var seed = await SeedAsync();
        var targetId = await AddDocumentAsync(seed, "Акт-основание");
        await AddDocumentAsync(seed, "Акт со ссылкой",
            $"{{'Основание':{{'$ref':'instance','instanceId':'{targetId}'}}}}");

        await SendAsync(new DeleteDocumentSetCommand(seed.SetId));

        Assert.Equal(0, await CountObjectsAsync());
    }

    /// <summary>То же для базового экземпляра: наследование внутри поддерева удалению не помеха.</summary>
    [Fact]
    public async Task DeletingSection_WithBaseRefInsideSubtree_Succeeds()
    {
        var seed = await SeedAsync();
        var baseId = await AddDocumentAsync(seed, "Базовый акт");
        await AddDocumentAsync(seed, "Наследник", $"{{'_baseRef':{{'kind':'document','id':'{baseId}'}}}}");

        await SendAsync(new DeleteSectionCommand(seed.SectionId));

        Assert.Equal(0, await CountObjectsAsync());
    }

    // ── Уборка ранее накопившихся сирот ──────────────────────────────────────────

    /// <summary>
    /// Инструмент обслуживания на сиротах, заведённых как их плодила прежняя ошибка: объект есть,
    /// комплекта нет. Сухой прогон обязан их видеть, боевой — убрать, повторный — найти ноль.
    /// </summary>
    [Fact]
    public async Task OrphanCleanup_FindsAndRemovesObjectsOfMissingScopes()
    {
        var seed = await SeedAsync();
        await AddDocumentAsync(seed, "Акт");

        // Сирота ровно того вида, что оставляло удаление раздела до этой правки.
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
            var orphan = DomainObject.Create(seed.DocTypeId, "Потерянный", J("{'Поле':'значение'}"),
                CatalogScope.Set, Guid.NewGuid());
            await repo.AddAsync(orphan);
            await repo.SaveChangesAsync();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<OrphanObjectCleanup>();

            var dry = await cleanup.RunAsync(dryRun: true);
            Assert.Equal(1, dry.Total);
            Assert.Equal(1, dry.Objects);
            Assert.Equal(1, dry.WithData); // непустой — повод посмотреть глазами, а не жать «удалить»
            Assert.Equal(2, await CountObjectsAsync()); // сухой прогон ничего не тронул

            var real = await cleanup.RunAsync(dryRun: false);
            Assert.Equal(1, real.Total);
            Assert.Equal(1, await CountObjectsAsync()); // живой документ на месте

            Assert.Equal(0, (await cleanup.RunAsync(dryRun: true)).Total);
        }
    }

    /// <summary>
    /// Сирота, на которую ещё ссылается ЖИВАЯ запись, уборкой не трогается. Ссылки резолвятся по
    /// идентификатору, а не по месту: такая ссылка работает по сей день, и снеси мы её цель —
    /// уборка своими руками сделала бы висячей ровно ту ссылку, ради которой всё затевалось.
    /// </summary>
    [Fact]
    public async Task OrphanCleanup_KeepsOrphansStillReferencedByLiveRecords()
    {
        var seed = await SeedAsync();
        Guid orphanId;
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
            var orphan = DomainObject.Create(seed.DocTypeId, "Потерянная запись", J("{'Поле':'значение'}"),
                CatalogScope.Set, Guid.NewGuid());
            await repo.AddAsync(orphan);
            await repo.SaveChangesAsync();
            orphanId = orphan.Id;
        }
        // Живой документ ссылается на сироту — ссылка по идентификатору, и она резолвится.
        await AddDocumentAsync(seed, "Живой акт", $"{{'Ссылка':{{'$ref':'catalog','entryId':'{orphanId}'}}}}");

        using (var scope = fixture.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<OrphanObjectCleanup>();
            var dry = await cleanup.RunAsync(dryRun: true);
            Assert.Equal(1, dry.Objects);
            Assert.Equal(1, dry.Referenced);
            Assert.Equal(0, dry.Total); // удалять нечего: единственная сирота держится ссылкой

            await cleanup.RunAsync(dryRun: false);
            Assert.Equal(2, await CountObjectsAsync()); // сирота на месте
        }
    }

    // ── Документы качества и связки на той же оси ────────────────────────────────

    /// <summary>
    /// Документ качества уровня комплекта и связки материалов висят на той же полиморфной оси, что и
    /// объекты, и внешнего ключа у них тоже нет. Не унеси их каскад — они остались бы сиротами: на
    /// рабочей базе таких документов 14, а связок 54, так что это не теоретическая полнота.
    /// </summary>
    [Fact]
    public async Task DeletingSet_RemovesQualityDocsAndLinksOfThatScope()
    {
        var seed = await SeedAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var cert = await M(scope).Send(new CreateQualityDocumentCommand(
                seed.DocTypeId, "Сертификат комплекта", J("{}"),
                CatalogScope.Set, seed.SetId, QualityDocSource.Manual, null, null, null));
            await M(scope).Send(new SetMaterialLinksCommand(
                CatalogScope.Set, seed.SetId, [new MaterialLinkInput("ВВГнг|3х2,5", "Кабель")], cert.Id));
        }

        await SendAsync(new DeleteDocumentSetCommand(seed.SetId));

        using var check = fixture.Services.CreateScope();
        var quality = await check.ServiceProvider.GetRequiredService<IRepository<QualityDocument>>()
            .FindAsync(_ => true);
        var links = await check.ServiceProvider.GetRequiredService<IRepository<MaterialQualityLink>>()
            .FindAsync(_ => true);
        Assert.Empty(quality);
        Assert.Empty(links);
    }

    /// <summary>
    /// Документ качества, лежащий В УДАЛЯЕМОМ комплекте, не должен считаться внешним держателем.
    /// Он уходит вместе с комплектом, ссылка исчезает целиком — а объяви мы его «ссылающимся
    /// извне», комплект стал бы неудаляемым из-за собственного содержимого, и сообщение об отказе
    /// называло бы документ, который лежит внутри.
    /// </summary>
    [Fact]
    public async Task DeletingSet_WithQualityDocOfSameScopeReferencingInside_Succeeds()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "Акт", "{'Номер':'7'}");
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new CreateQualityDocumentCommand(
                seed.DocTypeId, "Сертификат комплекта",
                J($"{{'Основание':{{'$ref':'document','instanceId':'{docId}','fieldKey':'Номер'}}}}"),
                CatalogScope.Set, seed.SetId, QualityDocSource.Manual, null, null, null));

        await SendAsync(new DeleteDocumentSetCommand(seed.SetId));

        Assert.Equal(0, await CountObjectsAsync());
    }

    /// <summary>А документ качества ОБЩЕЙ библиотеки — по-прежнему внешний держатель.</summary>
    [Fact]
    public async Task DeletingSet_WithSystemQualityDocReferencingInside_IsRejected()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "Акт", "{'Номер':'7'}");
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new CreateQualityDocumentCommand(
                seed.DocTypeId, "Сертификат библиотеки",
                J($"{{'Основание':{{'$ref':'document','instanceId':'{docId}','fieldKey':'Номер'}}}}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteDocumentSetCommand(seed.SetId)));
    }
}
