using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <param name="TypeName">Тип, у которого денежное поле оказалось бы в общей таблице, — сохраняемый либо его потомок.</param>
public record TagMoneyStorageViolation(string TagLabel, string TypeName, string FieldKey)
{
    public string Describe() =>
        $"Тэг «{TagLabel}» означает деньги, а тип «{TypeName}» хранится в общей таблице объектов: поле " +
        $"«{FieldKey}» прочитал бы любой вошедший — «Общие данные», наборы данных и поиск на права " +
        "модуля не смотрят. Суммы ведут в записях модуля (счёт, накладная), а не в этом типе.";
}

/// <summary>
/// Денежный тэг не ставится полю типа, который хранится в общей таблице объектов (ТЗ STG-6, COST-29;
/// задача H1, issue #1104).
///
/// <para>Вторая половина правила <c>ModuleMoneyStorage</c>. То проверяет ОБЪЯВЛЕНИЯ модулей при старте;
/// это — правку схемы в редакторе: открытому типу модуля и любому типу заказчика администратор вправе
/// дописать поле, и тэг «Итоговая сумма» предлагается всякому числовому полю. Без этой проверки сумма
/// легла бы в общую таблицу одним сохранением, а старт о ней не узнал бы.</para>
///
/// <para>⚠️ Тэг вне каталога — чужой или выключенного модуля — здесь не виден, как и у кратности: его
/// правил этот экземпляр не знает. Выключенный модуль своих тэгов не предлагает (TYPE-22), так что
/// поставить такой тэг в редакторе нечем.</para>
/// </summary>
public static class TagMoneyStorageValidator
{
    /// <param name="saving">Сохраняемый тип, УЖЕ несущий входящую схему — как у <see cref="TagCardinalityValidator" />.</param>
    public static IReadOnlyList<TagMoneyStorageViolation> Validate(
        TagCatalog catalog, DocumentType saving, IReadOnlyList<DocumentType> allDocTypes)
    {
        var effective = allDocTypes.Where(t => t.Id != saving.Id).Append(saving).ToList();

        // И потомки: поле предка наследуется, а потомок может лежать в общей таблице, даже если предок
        // абстрактный или хранится иначе.
        return [.. from type in effective
                   where type.Id == saving.Id || TagCardinalityValidator.IsDescendantOf(type, saving.Id, effective)
                   where type.Storage == TypeStorage.SharedObject
                   from field in SchemaTags.TaggedFieldsInSchemaOrder(type, effective)
                   let tag = catalog.Find(field.Tag.Code)
                   where tag is { Money: true }
                   select new TagMoneyStorageViolation(tag.Label, type.Name, field.Key)];
    }
}
