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
    /// <summary>Ключи полей идентичности у типов, способных нести документ качества.</summary>
    public static string[] KeysOf(IEnumerable<DocumentType> composites)
        => composites
            .Where(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.MaterialQualityDocLink).Count > 0)
            .SelectMany(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.Identity))
            .Distinct()
            .ToArray();
}
