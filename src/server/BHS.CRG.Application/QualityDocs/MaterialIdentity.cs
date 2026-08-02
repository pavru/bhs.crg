using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Какими полями опознаётся МАТЕРИАЛ при сопоставлении с документом качества (issue #569).
///
/// Тэг <see cref="FunctionalTag.Identity"/> живёт не только на материале: единица измерения
/// опознаётся своим наименованием, организация — сокращённым названием, и это правильно. Но раньше
/// все такие поля сваливались в один список и применялись к строкам материала — а в наборе данных
/// есть колонка «ЕдиницаИзмерения». В результате ключом КАЖДОЙ из 151 строки реестра материалов
/// становилась единица измерения: список схлопывался до четырёх («шт», «упак», «м», «компл»), а
/// связка, заведённая там, при генерации подтянула бы один сертификат ко всем материалам с этой
/// единицей.
///
/// Материал — это составной тип, который МОЖЕТ НЕСТИ документ качества, то есть имеет поле с тэгом
/// <see cref="FunctionalTag.MaterialQualityDocLink"/>. Его поля идентичности и берём.
/// </summary>
public static class MaterialIdentity
{
    /// <summary>
    /// Тип-материал: несёт поле документа качества сам или унаследовал его.
    ///
    /// Наследование учитываем не «на будущее»: клиент решает тот же вопрос через
    /// <c>resolveEffectiveFields</c>, то есть по всей цепочке. Разойдись эти две семантики —
    /// подтип материала показывался бы на вкладке, но при генерации не сопоставлялся, и документ
    /// качества молча не попал бы в PDF при здоровом виде в UI.
    /// </summary>
    public static bool IsMaterial(DocumentType type, IReadOnlyList<DocumentType> allTypes)
        => SchemaTags.TaggedFields(type, allTypes)
            .Any(f => f.Tag == FunctionalTag.MaterialQualityDocLink);

    /// <summary>
    /// Ключи полей идентичности у типов, способных нести документ качества, — В ПОРЯДКЕ компонентов
    /// составного ключа (issue #583): сначала параметр тэга («identity:1» перед «identity:2»), затем
    /// поля без номера в порядке схемы.
    ///
    /// Типы обходятся по идентификатору, а не в порядке выдачи хранилища: порядок строк без ORDER BY
    /// не гарантирован, а от него зависят ключи ВСЕХ материалов — «плавающий» порядок означал бы, что
    /// связки перестают находиться после перезапуска и никто не понимает почему.
    /// </summary>
    public static string[] KeysOf(IReadOnlyList<DocumentType> allTypes)
        => [.. allTypes
            .Where(t => IsMaterial(t, allTypes))
            .OrderBy(t => t.Id)
            .SelectMany(t => SchemaTags.OrderedKeysWithTag(t, allTypes, FunctionalTag.Identity))
            .Distinct()];

    /// <summary>Ключ поля, в которое подмешивается документ качества (тэг material.qualityDocLink).</summary>
    public static string? QualityDocFieldOf(IReadOnlyList<DocumentType> allTypes)
        => allTypes
            .SelectMany(t => SchemaTags.TaggedFields(t, allTypes))
            .Where(f => f.Tag == FunctionalTag.MaterialQualityDocLink)
            .Select(f => f.Key)
            .FirstOrDefault();
}
