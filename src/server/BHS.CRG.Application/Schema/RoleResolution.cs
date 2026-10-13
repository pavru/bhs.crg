using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>Что не так с полем роли. <see cref="None" /> — поле найдено и годится.</summary>
public enum RoleFieldFault
{
    None,

    /// <summary>Ни одно поле справочника тэг не несёт.</summary>
    Absent,

    /// <summary>Тэг несут несколько полей — какое читать, решить нечем.</summary>
    Several,

    /// <summary>Поле с тэгом есть, но не того вида, который тэг допускает, либо оно расчётное.</summary>
    WrongKind,

    /// <summary>
    /// У подтипа тэг стоит на другом поле (или не стоит вовсе): потомок исключил помеченное поле
    /// предка. Для схемы это законно, а справочник читают одним ключом на всю семью.
    /// </summary>
    DiffersInSubtype,
}

/// <summary>Справочник роли: кто её несёт и какие типы в него входят.</summary>
/// <param name="Bearers">Типы, несущие роль СОБСТВЕННОЙ схемой. Один — роль обеспечена; ни одного —
/// не назначена; больше одного — нарушение, которого обычное сохранение не допустит, а
/// восстановление копии и включение модуля — допустят.</param>
/// <param name="Family">Носитель и его потомки: идентификатор → код. Пусто, если роль не обеспечена.</param>
public sealed record ResolvedRole(
    string Role, IReadOnlyList<DocumentType> Bearers, IReadOnlyDictionary<Guid, string> Family)
{
    /// <summary>Единственный носитель; <c>null</c> — роль не обеспечена.</summary>
    public DocumentType? Root => Bearers.Count == 1 ? Bearers[0] : null;
}

/// <summary>Поле справочника, найденное по тэгу.</summary>
/// <param name="Key">Ключ поля; <c>null</c>, пока <see cref="Fault" /> не <see cref="RoleFieldFault.None" />.</param>
/// <param name="Keys">Все ключи, несущие тэг у носителя роли, — для слов о нарушении.</param>
/// <param name="Subtype">Подтип, у которого тэг стоит иначе, — для <see cref="RoleFieldFault.DiffersInSubtype" />.</param>
public sealed record ResolvedRoleField(
    string? Key, RoleFieldFault Fault, IReadOnlyList<string> Keys, SchemaFieldInfo? Field = null,
    DocumentType? Subtype = null);

/// <summary>
/// Справочник по роли и его поле по тэгу — ОДИН расчёт на всех, кто спрашивает (ТЗ TYPE-23,
/// CORE-20.4, issue #1101).
///
/// <para>Зовут его и порт, которым модуль читает справочник, и проверка покрытия, которую видит
/// администратор. Считай они каждый своё, экран однажды сказал бы «сопоставлено» про поле, которое
/// порт не находит, — ровно та неправда, ради устранения которой роль заведена.</para>
///
/// <para>Чистая функция: типы приходят снаружи, базы здесь нет.</para>
/// </summary>
public static class RoleResolution
{
    public static ResolvedRole Role(string roleTag, IReadOnlyList<DocumentType> all)
    {
        // Носитель — тот, у кого тэг в СОБСТВЕННОЙ схеме: так же считает потолок MaxBearers. Подтип
        // роль наследует, но носителем не становится.
        var bearers = all.Where(t => SchemaTags.SchemaHasTypeTag(t.Schema, roleTag)).ToList();
        return new(roleTag, bearers, bearers.Count == 1 ? Family(bearers[0], all) : new Dictionary<Guid, string>());
    }

    public static ResolvedRoleField Field(ResolvedRole role, TagDefinition tag, IReadOnlyList<DocumentType> all)
    {
        if (role.Root is not { } root) return new(null, RoleFieldFault.Absent, []);

        var keys = KeysWithTag(root, all, tag.Code);
        if (keys.Count == 0) return new(null, RoleFieldFault.Absent, keys);
        if (keys.Count > 1) return new(null, RoleFieldFault.Several, keys);

        var byId = all.ToDictionary(t => t.Id);
        var field = DocumentTypeSchemaReader.EffectiveFields(root.Id, byId).FirstOrDefault(f => f.Key == keys[0]);
        if (field is null) return new(null, RoleFieldFault.Absent, keys);

        // Вид сверяем с объявлением тэга: сервер при сохранении схемы его не проверяет, и поле
        // можно сделать составным, оставив тэг. Расчётное значения не хранит — читать в нём нечего.
        if (field.Computed || (tag.AppliesTo.Length > 0 && !tag.AppliesTo.Contains(field.Type, StringComparer.Ordinal)))
            return new(null, RoleFieldFault.WrongKind, keys, field);

        foreach (var id in role.Family.Keys.Where(id => id != root.Id))
        {
            var own = KeysWithTag(byId[id], all, tag.Code);
            if (own.Count != 1 || own[0] != keys[0])
                return new(null, RoleFieldFault.DiffersInSubtype, keys, field, byId[id]);
        }

        return new(keys[0], RoleFieldFault.None, keys, field);
    }

    /// <summary>Ключи с тэгом — тем же счётом, каким кратность проверяется при сохранении схемы.</summary>
    private static List<string> KeysWithTag(DocumentType type, IReadOnlyList<DocumentType> all, string tag) =>
        [.. SchemaTags.TaggedFieldsInSchemaOrder(type, all)
            .Where(f => f.Tag.Code == tag)
            .Select(f => f.Key)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Тип и все его потомки: идентификатор → код. Обход итеративный и слоями — цепочка родителей в
    /// базе ничем не мешает циклу, а рекурсия по ней кончилась бы переполнением стека вместо отказа.
    /// </summary>
    public static Dictionary<Guid, string> Family(DocumentType root, IReadOnlyList<DocumentType> all)
    {
        var family = new Dictionary<Guid, string> { [root.Id] = root.Code };
        var frontier = new List<Guid> { root.Id };

        while (frontier.Count > 0)
        {
            var children = all.Where(t => t.ParentId is { } p && frontier.Contains(p) && !family.ContainsKey(t.Id))
                .ToList();
            foreach (var child in children) family[child.Id] = child.Code;
            frontier = [.. children.Select(t => t.Id)];
        }

        return family;
    }
}
