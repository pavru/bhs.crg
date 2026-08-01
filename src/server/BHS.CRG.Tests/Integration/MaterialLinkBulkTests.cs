using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Пакетные операции над связями «материал → документ качества» (issue #556).
///
/// Сценарий не выдуманный: на живых данных у одного сертификата 69 связок, из которых верна одна
/// (#552) — чинить такое по одной строке невозможно, поэтому пакет обязан работать и обязан честно
/// отчитываться о том, что реально снял.
/// </summary>
[Collection("Integration")]
public class MaterialLinkBulkTests(IntegrationTestFixture fx)
{
    private static IMediator M(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IMediator>();
    private static string Code(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12];

    /// <summary>
    /// Каждый шаг — в СВОЁМ scope, то есть со своим DbContext: ровно так работает боевой путь, где
    /// шаг это HTTP-запрос. Внутри одного контекста тест падал бы на отслеживании — репозиторий
    /// читает через AsNoTracking, и Remove прикреплял бы вторую копию уже отслеживаемой сущности.
    /// </summary>
    private async Task<T> InScopeAsync<T>(Func<IMediator, Task<T>> action)
    {
        using var scope = fx.Services.CreateScope();
        return await action(M(scope));
    }

    /// <summary>Сертификат со своим типом: имя типа обязано быть уникальным — база у тестов общая.</summary>
    private Task<Guid> SeedCertAsync() => InScopeAsync(async m =>
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var certType = await m.Send(new CreateDocumentTypeCommand(
            $"Сертификат {suffix}", Code("CERT"), DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var doc = await m.Send(new CreateQualityDocumentCommand(
            certType.Id, $"Сертификат {suffix}", JsonDocument.Parse("{}"),
            CatalogScope.System, null, QualityDocSource.Manual, null, null, null));
        return doc.Id;
    });

    private Task<List<MaterialLinkRow>> LinksAsync(string keyPrefix) => InScopeAsync(async m =>
        (await m.Send(new ListMaterialLinksQuery(CatalogScope.System, null)))
            .Where(l => l.MaterialKey.StartsWith(keyPrefix)).ToList());

    private Task<int> LinkAsync(Guid docId, params string[] keys) => InScopeAsync(m =>
        m.Send(new SetMaterialLinksCommand(CatalogScope.System, null,
            keys.Select(k => new MaterialLinkInput(k)).ToList(), docId)));

    private Task<int> RemoveAsync(params Guid[] ids) => InScopeAsync(m =>
        m.Send(new RemoveMaterialLinksCommand(ids)));

    [Fact]
    public async Task RemovesOnlyRequestedLinks_AndReportsWhatWasActuallyRemoved()
    {
        var docId = await SeedCertAsync();
        var key = Code("k");
        await LinkAsync(docId, $"{key}-1", $"{key}-2", $"{key}-3");

        var links = await LinksAsync(key);
        Assert.Equal(3, links.Count);

        Assert.Equal(2, await RemoveAsync(links[0].Id, links[1].Id));

        var left = await LinksAsync(key);
        Assert.Equal(links[2].MaterialKey, Assert.Single(left).MaterialKey);
    }

    /// <summary>
    /// Отчитываемся СНЯТЫМ, а не запрошенным: строка могла исчезнуть в параллельной сессии, и
    /// «снято 2» при фактическом одном было бы неправдой в лицо пользователю.
    /// </summary>
    [Fact]
    public async Task UnknownIds_AreCountedAsNotRemoved()
    {
        var docId = await SeedCertAsync();
        var key = Code("k");
        await LinkAsync(docId, key);
        var link = Assert.Single(await LinksAsync(key));

        Assert.Equal(1, await RemoveAsync(link.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task EmptyRequest_RemovesNothing()
    {
        Assert.Equal(0, await RemoveAsync());
    }

    /// <summary>Повтор одного и того же идентификатора не должен считаться дважды.</summary>
    [Fact]
    public async Task DuplicateIds_AreCountedOnce()
    {
        var docId = await SeedCertAsync();
        var key = Code("k");
        await LinkAsync(docId, key);
        var link = Assert.Single(await LinksAsync(key));

        Assert.Equal(1, await RemoveAsync(link.Id, link.Id));
    }
}
