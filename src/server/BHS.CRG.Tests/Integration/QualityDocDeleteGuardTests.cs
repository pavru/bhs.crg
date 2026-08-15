using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Guard удаления по обратным ссылкам — обе стороны (issue #735).
///
/// <para>Асимметрия, ради которой заведена issue: удаление документа комплекта блокировалось
/// проверкой ссылающихся, а удаление документа качества сносило только связки материалов. До #733
/// оберегать было нечего — <c>$ref:"instance"</c> на документ качества резолвер не находил вовсе;
/// теперь это рабочая ссылка второго домена.</para>
///
/// <para>Зеркальная сторона проверяется здесь же: реквизиты документа качества проходят тот же
/// <c>ResolveNode</c>, значит «$ref» в них — такая же рабочая ссылка, и удаление ЕЁ цели тоже
/// обязано блокироваться. Прежний сканер смотрел только в <c>domain_objects</c> и этой стороны
/// не видел.</para>
/// </summary>
[Collection("Integration")]
public class QualityDocDeleteGuardTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private readonly Guid _userId = Guid.NewGuid();

    private sealed record Seed(Guid SetId, Guid DocTypeId, Guid CertTypeId);

    private async Task<Seed> SeedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var construction = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        var docType = await m.Send(new CreateDocumentTypeCommand(
            "Акт", "AOSR", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        var certType = await m.Send(new CreateDocumentTypeCommand(
            "Сертификат", "CERT", DocumentTypeKind.Document, null, J("{'fields':[]}")));
        return new Seed(set.Id, docType.Id, certType.Id);
    }

    private async Task<QualityDocument> AddQualityAsync(Guid typeId, string name, string requisites, Guid? setId)
    {
        using var scope = fixture.Services.CreateScope();
        return await M(scope).Send(new CreateQualityDocumentCommand(
            typeId, name, J(requisites),
            setId is null ? CatalogScope.System : CatalogScope.Set, setId,
            QualityDocSource.Manual, null, null, null));
    }

    /// <summary>Документ комплекта; <paramref name="requisites"/> — сырой JSON реквизитов.</summary>
    private async Task<Guid> AddDocumentAsync(Seed seed, string requisites, string? name = null)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(seed.SetId, seed.DocTypeId));
        if (name is not null) await M(scope).Send(new RenameDocumentInstanceCommand(inst.Id, name));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        return inst.Id;
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = fixture.Services.CreateScope();
        await M(scope).Send(request);
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await M(scope).Send(request);
    }

    // ── Сторона из #735: удаляют документ качества ───────────────────────────────

    [Fact]
    public async Task DeletingQualityDoc_ReferencedByDocumentRequisites_IsRejected()
    {
        var seed = await SeedAsync();
        var cert = await AddQualityAsync(seed.CertTypeId, "EKF — автоматы", "{'НомерДок':'ЕАЭС RU С-CN.1'}", seed.SetId);
        await AddDocumentAsync(seed, $"{{'Качество':{{'$ref':'instance','instanceId':'{cert.Id}'}}}}",
            name: "Акт освидетельствования №7");

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteQualityDocumentCommand(cert.Id)));

        // Отказ обязан НАЗЫВАТЬ держателя ссылки: без имени человеку негде её искать.
        Assert.Contains("Акт освидетельствования №7", ex.Message);
    }

    /// <summary>
    /// Ссылка из общих данных — тот же отказ. Проверяется отдельно от документа комплекта, потому что
    /// это разные экраны и разные команды удаления, а сканируются они одним проходом: сломайся охват,
    /// одна из двух дверей осталась бы открытой при зелёном тесте на другую.
    /// </summary>
    [Fact]
    public async Task DeletingQualityDoc_ReferencedByCommonDataEntry_IsRejected()
    {
        var seed = await SeedAsync();
        var cert = await AddQualityAsync(seed.CertTypeId, "Декларация ЭКФ", "{}", null);
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new CreateCommonDataEntryCommand(
                "Кабель ВВГнг 3х2,5", seed.DocTypeId,
                J($"{{'Сертификат':{{'$ref':'instance','instanceId':'{cert.Id}'}}}}"),
                CatalogScope.System, null));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteQualityDocumentCommand(cert.Id)));

        Assert.Contains("Кабель ВВГнг 3х2,5", ex.Message);
    }

    /// <summary>
    /// Связка материала документ уносит с собой — это его собственный хвост, а не чужая ссылка.
    /// Считай guard связки ссылающимися, библиотека стала бы неудаляемой: связка есть почти у каждого
    /// документа качества, ради них он и заводится.
    /// </summary>
    [Fact]
    public async Task DeletingQualityDoc_WithMaterialLinksOnly_Succeeds()
    {
        var seed = await SeedAsync();
        var cert = await AddQualityAsync(seed.CertTypeId, "Сертификат на кабель", "{}", null);
        await SendAsync(new SetMaterialLinksCommand(
            CatalogScope.System, null, [new MaterialLinkInput("ВВГнг|3х2,5", "Кабель")], cert.Id));

        await SendAsync(new DeleteQualityDocumentCommand(cert.Id));

        using var scope = fixture.Services.CreateScope();
        Assert.Null(await M(scope).Send(new GetQualityDocumentQuery(cert.Id)));
        // Связка ушла вместе с документом — иначе экран контроля показал бы строку с пустым именем.
        var links = await scope.ServiceProvider.GetRequiredService<IRepository<MaterialQualityLink>>()
            .FindAsync(l => l.QualityDocumentId == cert.Id);
        Assert.Empty(links);
    }

    /// <summary>Ссылку убрали — удаление проходит: guard не должен становиться билетом в один конец.</summary>
    [Fact]
    public async Task DeletingQualityDoc_AfterReferenceRemoved_Succeeds()
    {
        var seed = await SeedAsync();
        var cert = await AddQualityAsync(seed.CertTypeId, "Сертификат", "{}", seed.SetId);
        var docId = await AddDocumentAsync(seed, $"{{'Качество':{{'$ref':'instance','instanceId':'{cert.Id}'}}}}");

        await SendAsync(new UpdateRequisitesCommand(docId, J("{}")));
        await SendAsync(new DeleteQualityDocumentCommand(cert.Id));

        using var scope = fixture.Services.CreateScope();
        Assert.Null(await M(scope).Send(new GetQualityDocumentQuery(cert.Id)));
    }

    // ── Зеркальная сторона: ссылается САМ документ качества ──────────────────────
    //
    // Формы здесь ровно те, что резолвер разворачивает ВНУТРИ реквизитов документа качества
    // (он обходит их с allowInstanceRefs: false): «catalog» — запись целиком, «document» —
    // протягивание одного поля. Граница — тестом ниже.

    [Fact]
    public async Task DeletingCommonDataEntry_ReferencedByQualityDocRequisites_IsRejected()
    {
        var seed = await SeedAsync();
        Guid entryId;
        using (var scope = fixture.Services.CreateScope())
            entryId = (await M(scope).Send(new CreateCommonDataEntryCommand(
                "ООО «Завод»", seed.DocTypeId, J("{}"), CatalogScope.System, null))).Id;

        await AddQualityAsync(seed.CertTypeId, "Сертификат завода",
            $"{{'Изготовитель':{{'$ref':'catalog','entryId':'{entryId}'}}}}", null);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteCommonDataEntryCommand(entryId)));

        // Род держателя назван прямо: имя без него не сказало бы, что искать в библиотеке, а не в комплекте.
        Assert.Contains("документ качества «Сертификат завода»", ex.Message);
    }

    [Fact]
    public async Task DeletingDocumentInstance_ReferencedByQualityDocFieldRef_IsRejected()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "{'Номер':'7'}");
        await AddQualityAsync(seed.CertTypeId, "Протокол испытаний",
            $"{{'Основание':{{'$ref':'document','instanceId':'{docId}','fieldKey':'Номер'}}}}", seed.SetId);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new DeleteDocumentInstanceCommand(docId)));

        Assert.Contains("документ качества «Протокол испытаний»", ex.Message);
    }

    /// <summary>
    /// Граница guard'а. <c>$ref:"instance"</c> внутри реквизитов документа качества резолвер НЕ
    /// разворачивает: реквизиты обходятся с <c>allowInstanceRefs: false</c>, и такая ссылка отдаётся
    /// как <c>StripRef</c> — стаб из самого узла, в базу за целью резолвер не ходит. Блокируй мы
    /// удаление и по ней, документ-цель стал бы неудаляемым из-за указателя, которым генерация не
    /// пользуется, — запрет без выигрыша.
    /// </summary>
    [Fact]
    public async Task DeletingDocumentInstance_ReferencedByQualityDocInstanceRef_IsAllowed()
    {
        var seed = await SeedAsync();
        var docId = await AddDocumentAsync(seed, "{}");
        await AddQualityAsync(seed.CertTypeId, "Сертификат со стабом",
            $"{{'Основание':{{'$ref':'instance','instanceId':'{docId}'}}}}", seed.SetId);

        await SendAsync(new DeleteDocumentInstanceCommand(docId));

        using var scope = fixture.Services.CreateScope();
        Assert.Null(await M(scope).Send(new GetDocumentInstanceQuery(docId)));
    }
}
