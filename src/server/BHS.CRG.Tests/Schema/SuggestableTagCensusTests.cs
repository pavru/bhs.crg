using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Тэг <c>input.noSuggest</c> в реестре и перепись строковых тэгов ядра для правила
/// <see cref="Suggestable" /> (issue #1529).
/// </summary>
public class SuggestableTagCensusTests
{
    /// <summary>
    /// Строковые тэги поля ядра, которые однозначными НЕ считаются, — с причиной. Вместе с
    /// <see cref="Suggestable.UniqueTags" /> это полное решение по каждому такому тэгу.
    /// </summary>
    private static readonly Dictionary<string, string> NotUnique = new(StringComparer.Ordinal)
    {
        [FunctionalTag.DocDate] = "даты повторяются у многих документов",
        [FunctionalTag.EmployeePosition] = "должность общая у многих сотрудников",
        [FunctionalTag.EmployeeAccount] = "поле заперто ядром (CoreRecordTypes) — его отсекает «locked»",
        [FunctionalTag.NomenclatureUnit] = "единица общая у многих позиций — подсказка ей и нужна",
        [FunctionalTag.InputNoSuggest] = "сам тэг выключения — его код «manual»",
    };

    /// <summary>
    /// Новый строковый тэг без решения «однозначен / нет» сделал бы поле подсказываемым молча:
    /// правило о нём просто не знало бы. Отказ — с названием тэга.
    /// </summary>
    [Fact]
    public void Каждый_строковый_тэг_ядра_решён()
    {
        var unique = Suggestable.UniqueTags.Select(u => u.Tag).ToHashSet(StringComparer.Ordinal);
        var stringTags = TagRegistry.Core
            .Where(t => t.Scope == TagScope.Field && t.AppliesTo.Contains("string", StringComparer.Ordinal))
            .Select(t => t.Code)
            .ToList();

        Assert.All(stringTags, code => Assert.True(unique.Contains(code) ^ NotUnique.ContainsKey(code),
            $"строковый тэг «{code}»: решите в Suggestable.UniqueTags или здесь в NotUnique, однозначен ли он "
            + "(ровно в одном месте)"));
    }

    /// <summary>Решение о тэге, которого в реестре нет или который не строковый, — опечатка в коде тэга.</summary>
    [Fact]
    public void Решения_только_о_строковых_тэгах_реестра()
    {
        var stringTags = TagRegistry.Core
            .Where(t => t.Scope == TagScope.Field && t.AppliesTo.Contains("string", StringComparer.Ordinal))
            .Select(t => t.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Suggestable.UniqueTags.Select(u => u.Tag).Concat(NotUnique.Keys),
            code => Assert.True(stringTags.Contains(code), $"«{code}» — не строковый тэг поля ядра"));
    }

    /// <summary>Тэг собран в реестр так, как его отдаёт <c>GET /api/tags</c> (<c>TagCatalog.All</c>).</summary>
    [Fact]
    public void Тэг_не_подсказывать_в_реестре()
    {
        var tag = TagCatalog.Build(TagRegistry.Core, []).Find(FunctionalTag.InputNoSuggest);

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
