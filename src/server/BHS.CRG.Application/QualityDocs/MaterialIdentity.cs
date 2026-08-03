using System.Text.Json;
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
        => SchemaTags.TaggedFieldsInSchemaOrder(type, allTypes)
            .Any(f => f.Tag.Code == FunctionalTag.MaterialQualityDocLink);

    /// <summary>
    /// Ключи полей идентичности у типов, способных нести документ качества, — В ПОРЯДКЕ компонентов
    /// составного ключа (issue #583): сначала параметр тэга («identity:1» перед «identity:2»), затем
    /// поля без номера.
    ///
    /// Номер сравнивается СКВОЗЬ типы, а не внутри каждого: пользователь видит номера как сквозную
    /// нумерацию компонентов ключа, и «identity:1» подтипа кабеля обязан стоять перед «identity:2»
    /// базового материала, иначе номера означали бы разное в зависимости от того, где поле объявлено.
    ///
    /// Типы обходятся по идентификатору, а не в порядке выдачи хранилища: порядок строк без ORDER BY
    /// не гарантирован, а от него зависят ключи ВСЕХ материалов — «плавающий» порядок означал бы, что
    /// связки перестают находиться после перезапуска и никто не понимает почему. Сравнение —
    /// ПОСТРОКОВОЕ (ordinal), а не <see cref="Guid.CompareTo(Guid)" />: те же ключи строит клиент
    /// (<c>materialIdentityKeys</c>), а он умеет сравнивать идентификаторы только строками. Разойдись
    /// эти два порядка — ключ связки, заведённой в UI, не совпал бы с ключом на генерации.
    /// </summary>
    public static string[] KeysOf(IReadOnlyList<DocumentType> allTypes)
        => [.. allTypes
            .Where(t => IsMaterial(t, allTypes))
            .OrderBy(t => t.Id.ToString(), StringComparer.Ordinal)
            .SelectMany((t, typeIndex) => SchemaTags.TaggedFieldsInSchemaOrder(t, allTypes)
                .Select((f, fieldIndex) => (f.Key, f.Tag, typeIndex, fieldIndex)))
            .Where(x => x.Tag.Code == FunctionalTag.Identity)
            .OrderBy(x => x.Tag.SortKey)
            .ThenBy(x => x.typeIndex)
            .ThenBy(x => x.fieldIndex)   // стабильность: поля без номера идут в порядке схемы
            .Select(x => x.Key)
            .Distinct()];

    /// <summary>
    /// Заполнено ли у материала поле документа качества.
    ///
    /// Один предикат на подмешивание и на проверку: резолвер по нему решает, не перетереть ли
    /// заданное вручную, а сканер — не предупредить ли о пропаже. Пока копий было две, они
    /// разошлись на пустом массиве — тот считался заполненным у резолвера и пустым у сканера, то
    /// есть материал одновременно получал предупреждение «привязка не найдена» и пропускался при
    /// подмешивании существующей связки.
    /// </summary>
    public static bool HasQualityDoc(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            JsonValueKind.Object => v.EnumerateObject().Any(),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            _ => true,
        };
    }

    /// <summary>Ключ поля, в которое подмешивается документ качества (тэг material.qualityDocLink).</summary>
    public static string? QualityDocFieldOf(IReadOnlyList<DocumentType> allTypes)
        => allTypes
            .SelectMany(t => SchemaTags.TaggedFields(t, allTypes))
            .Where(f => f.Tag == FunctionalTag.MaterialQualityDocLink)
            .Select(f => f.Key)
            .FirstOrDefault();
}
