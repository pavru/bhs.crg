using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Имя документа качества уникально в своей области (issue #588).
///
/// Живой случай: два сертификата назывались «EKF — автоматические выключатели», а внутри были разные
/// номера (RU C-CN.HA46.B.06753/23 и ЕАЭС RU C-CN.АД07.B.05521/23), разные органы и разные области
/// продукции (AV-125 против AV-6 и AV-10). Выбирая из списка, человек не видел, какой берёт.
/// </summary>
[Collection("Integration")]
public class QualityDocNameUniquenessTests(IntegrationTestFixture fx)
{
    private static IMediator M(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<T> InScopeAsync<T>(Func<IMediator, Task<T>> action)
    {
        using var scope = fx.Services.CreateScope();
        return await action(M(scope));
    }

    private Task<Guid> SeedTypeAsync() => InScopeAsync(async m =>
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var type = await m.Send(new CreateDocumentTypeCommand(
            $"Сертификат {suffix}", $"CERT{suffix}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        return type.Id;
    });

    private Task<QualityDocument> CreateAsync(Guid typeId, string name, CatalogScope scope, Guid? scopeId)
        => InScopeAsync(m => m.Send(new CreateQualityDocumentCommand(
            typeId, name, JsonDocument.Parse("{}"), scope, scopeId, QualityDocSource.Manual, null, null, null)));

    [Fact]
    public async Task SameNameInSameScope_IsRejected()
    {
        var typeId = await SeedTypeAsync();
        var name = $"EKF — автоматические выключатели {Guid.NewGuid():N}";
        await CreateAsync(typeId, name, CatalogScope.System, null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAsync(typeId, name, CatalogScope.System, null));
    }

    /// <summary>Сравнение без регистра и краевых пробелов — иначе запрет обходится пробелом, и в
    /// списке снова два неразличимых имени.</summary>
    [Fact]
    public async Task CaseAndSpaces_DoNotSlipThrough()
    {
        var typeId = await SeedTypeAsync();
        var name = $"Сертификат ЭКФ {Guid.NewGuid():N}";
        await CreateAsync(typeId, name, CatalogScope.System, null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAsync(typeId, $"  {name.ToUpperInvariant()} ", CatalogScope.System, null));
    }

    /// <summary>Разные области — разные списки: имя, занятое на комплекте, общей библиотеке не мешает.</summary>
    [Fact]
    public async Task SameNameInAnotherScope_IsAllowed()
    {
        var typeId = await SeedTypeAsync();
        var name = $"Сертификат {Guid.NewGuid():N}";
        await CreateAsync(typeId, name, CatalogScope.System, null);

        var setScoped = await CreateAsync(typeId, name, CatalogScope.Set, Guid.NewGuid());
        Assert.Equal(name, setScoped.DisplayName);
    }

    /// <summary>Правка документа не должна спотыкаться о его собственное имя.</summary>
    [Fact]
    public async Task UpdatingDocumentKeepingItsOwnName_IsAllowed()
    {
        var typeId = await SeedTypeAsync();
        var name = $"Сертификат {Guid.NewGuid():N}";
        var doc = await CreateAsync(typeId, name, CatalogScope.System, null);

        var updated = await InScopeAsync(m => m.Send(new UpdateQualityDocumentCommand(
            doc.Id, typeId, name, JsonDocument.Parse("""{"Продукция":"Выключатели"}"""))));

        Assert.Equal(name, updated.DisplayName);
    }

    /// <summary>
    /// Документ-дубль, заведённый ДО запрета (а такие в живой базе есть — ради них issue и заведена),
    /// обязан оставаться редактируемым. Иначе его нельзя ни поправить, ни распознать, пока не
    /// переименуешь, — то есть запрет наказывал бы за то, что случилось раньше него.
    /// </summary>
    [Fact]
    public async Task ExistingDuplicate_CanStillBeEdited_WhenNameUnchanged()
    {
        var typeId = await SeedTypeAsync();
        var name = $"EKF — автоматические выключатели {Guid.NewGuid():N}";
        var first = await CreateAsync(typeId, name, CatalogScope.System, null);
        // Второй дубль заводим в обход команды — так, как его создаёт импорт из интернета.
        QualityDocument second;
        using (var scope = fx.Services.CreateScope())
        {
            var repo = scope.ServiceProvider
                .GetRequiredService<BHS.CRG.Application.Common.IRepository<QualityDocument>>();
            second = QualityDocument.Create(typeId, name, JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Web, sourceUrl: $"https://example.test/{Guid.NewGuid():N}");
            await repo.AddAsync(second);
            await repo.SaveChangesAsync();
        }

        var updated = await InScopeAsync(m => m.Send(new UpdateQualityDocumentCommand(
            second.Id, typeId, name, JsonDocument.Parse("""{"Продукция":"Выключатели AV-6"}"""))));

        Assert.Equal(name, updated.DisplayName);
        Assert.NotEqual(first.Id, updated.Id);
    }

    [Fact]
    public async Task RenamingOntoAnotherDocumentName_IsRejected()
    {
        var typeId = await SeedTypeAsync();
        var first = $"Первый {Guid.NewGuid():N}";
        var second = $"Второй {Guid.NewGuid():N}";
        await CreateAsync(typeId, first, CatalogScope.System, null);
        var doc = await CreateAsync(typeId, second, CatalogScope.System, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(
            m => m.Send(new UpdateQualityDocumentCommand(doc.Id, typeId, first, JsonDocument.Parse("{}")))));
    }
}
