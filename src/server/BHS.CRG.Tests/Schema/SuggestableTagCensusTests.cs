using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Тэг <c>input.noSuggest</c> в реестре и перепись строковых тэгов поля для правила
/// <see cref="Suggestable" /> (issue #1529).
/// </summary>
public class SuggestableTagCensusTests
{
    /// <summary>
    /// Строковые тэги поля, которые однозначными НЕ считаются, — с причиной. Вместе с
    /// <see cref="Suggestable.UniqueTags" /> это полное решение по каждому такому тэгу.
    /// </summary>
    private static readonly Dictionary<string, string> NotUnique = new(StringComparer.Ordinal)
    {
        [FunctionalTag.DocDate] = "даты повторяются у многих документов",
        // Решение владельца 23.10.2026 (issue #1533): у заказчика его несёт «Номенклатура → Производитель».
        [FunctionalTag.Identity] = "компонент составного ключа: сам по себе не уникален, повтор ключа ловит сверка двойников",
        [FunctionalTag.EmployeePosition] = "должность общая у многих сотрудников",
        [FunctionalTag.NomenclatureUnit] = "единица общая у многих позиций — подсказка ей и нужна",
        [FunctionalTag.InputNoSuggest] = "сам тэг выключения — его код «manual»",
        // Модуль исполнительной документации.
        [FunctionalTag.DocPageCount] = "число страниц повторяется; значение кладёт генерация",
        [FunctionalTag.DocGeneratedAt] = "даты повторяются; значение кладёт генерация",
        [FunctionalTag.DocGeneratedBy] = "публикаторов немного — подсказка ровно то, что нужно",
        [FunctionalTag.QualityManufacturer] = "производитель общий у многих документов",
    };

    /// <summary>
    /// Реестр со ВСЕМИ модулями поставки, собранный так, как его собирает приложение: тэг модуля
    /// значит для правила то же, что тэг ядра, и забытый строковый тэг модуля сделал бы поле
    /// подсказываемым так же молча.
    /// </summary>
    private static readonly TagCatalog Catalog = BuildCatalog();

    private static TagCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleRegistry(DeliveredModules.All(), []));
        services.AddTagCatalog();
        return services.BuildServiceProvider().GetRequiredService<TagCatalog>();
    }

    private static HashSet<string> StringFieldTags() =>
    [
        .. Catalog.All
            .Where(t => t.Scope == TagScope.Field && t.AppliesTo.Contains("string", StringComparer.Ordinal))
            .Select(t => t.Code),
    ];

    /// <summary>
    /// Новый строковый тэг без решения «однозначен / нет» сделал бы поле подсказываемым молча:
    /// правило о нём просто не знало бы. Отказ — с названием тэга.
    /// </summary>
    [Fact]
    public void Каждый_строковый_тэг_решён()
    {
        var unique = Suggestable.UniqueTags.Select(u => u.Tag).ToHashSet(StringComparer.Ordinal);

        Assert.All(StringFieldTags(), code => Assert.True(unique.Contains(code) ^ NotUnique.ContainsKey(code),
            $"строковый тэг «{code}»: решите в Suggestable.UniqueTags или здесь в NotUnique, однозначен ли он "
            + "(ровно в одном месте)"));
    }

    /// <summary>Решение о тэге, которого в реестре нет или который не строковый, — опечатка в коде тэга.</summary>
    [Fact]
    public void Решения_только_о_строковых_тэгах_реестра()
    {
        var stringTags = StringFieldTags();

        Assert.All(Suggestable.UniqueTags.Select(u => u.Tag).Concat(NotUnique.Keys),
            code => Assert.True(stringTags.Contains(code), $"«{code}» — не строковый тэг поля в реестре"));
    }

    /// <summary>Тэг собран в реестр так, как его отдаёт <c>GET /api/tags</c> (<c>TagCatalog.All</c>).</summary>
    [Fact]
    public void Тэг_не_подсказывать_в_реестре()
    {
        var tag = Catalog.Find(FunctionalTag.InputNoSuggest);

        Assert.NotNull(tag);
        Assert.False(string.IsNullOrWhiteSpace(tag.Description));
        Assert.Equal(TagScope.Field, tag.Scope);
        Assert.Equal(["string"], tag.AppliesTo);
        Assert.True(tag.Multiple);
        Assert.Equal(TagOwners.Core, tag.Owner);
        Assert.Null(tag.Role);
        Assert.Null(tag.Restriction);
    }
}
