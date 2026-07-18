using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Профиль уровня (issue #258): сопоставление контейнерных уровней с тэгами профиль-типов и ключами в
/// data.уровень, плюс резолв профиль-типа по тэгу (единственный тип с profile-* в СОБСТВЕННОЙ схеме).
/// </summary>
public static class LevelProfiles
{
    /// <summary>Три контейнерных уровня: (уровень, тэг профиль-типа, ключ в data.уровень).</summary>
    public static readonly (CatalogScope Level, string Tag, string Key)[] Levels =
    [
        (CatalogScope.Construction, FunctionalTag.ProfileConstruction, "стройка"),
        (CatalogScope.Section, FunctionalTag.ProfileSection, "раздел"),
        (CatalogScope.Set, FunctionalTag.ProfileSet, "комплект"),
    ];

    public static string? TagFor(CatalogScope level)
    {
        foreach (var l in Levels) if (l.Level == level) return l.Tag;
        return null;
    }

    /// <summary>Id профиль-типа для тэга: единственный тип с тэгом в СОБСТВЕННОЙ схеме. При >1 —
    /// детерминированно первый по имени (лимит MaxBearers=1 обычно не даёт >1, но подстраховка).</summary>
    public static Guid? ResolveProfileTypeId(IEnumerable<DocumentType> allTypes, string tag) =>
        allTypes.Where(t => SchemaTags.SchemaHasTypeTag(t.Schema, tag))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefault();
}
