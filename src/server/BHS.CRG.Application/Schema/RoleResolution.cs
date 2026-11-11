using BHS.CRG.Application.Documents;
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
    /// У подтипа тэг стоит на ДРУГОМ поле: потомок исключил помеченное поле предка и пометил своё.
    /// Для схемы это законно, а справочник читают одним ключом на всю семью — у записей подтипа
    /// значение лежало бы не там, где его ищут.
    ///
    /// <para>Подтип, который поле просто исключил, нарушением НЕ считается: у его записей значения
    /// нет, и только. Так было и до ролей — остальной справочник из-за него не останавливался
    /// (ревью PR #1317).</para>
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
    /// <summary>
    /// Единственный носитель, ГОДНЫЙ в справочники; <c>null</c> — роль не обеспечена.
    ///
    /// <para>Годность проверяется здесь, а не у каждого читателя (ревью PR #1317). Сохранение схемы
    /// негодному типу роль не даст, но восстановление копии и правка базы мимо приложения — дадут; и
    /// тогда один читатель падал бы при старте («это Document, не составной тип»), второй отвечал
    /// 500, третий молча читал пустоту. Теперь все трое говорят одно: роль не обеспечена.</para>
    /// </summary>
    public DocumentType? Root => Bearers.Count == 1 && RoleResolution.IsDirectory(Bearers[0]) ? Bearers[0] : null;

    /// <summary>Единственный носитель, который справочником быть не может; иначе <c>null</c>.</summary>
    public DocumentType? Unfit => Bearers.Count == 1 && !RoleResolution.IsDirectory(Bearers[0]) ? Bearers[0] : null;

    /// <summary>Почему роль не обеспечена — словами для человека; <c>null</c>, если обеспечена.</summary>
    public string? Describe(TagDefinition role) => this switch
    {
        { Root: not null } => null,
        { Unfit: { } unfit } =>
            $"роль «{role.Label}» стоит на типе «{unfit.Name}», а справочником он быть не может: " +
            "справочник — составной тип, записи которого лежат в общих данных",
        { Bearers.Count: > 1 } =>
            $"роль «{role.Label}» несут несколько типов " +
            $"({string.Join(", ", Bearers.Select(t => $"«{t.Name}»"))}) — какой из них справочник, решить нечем",
        _ => $"роль «{role.Label}» не назначена ни одному типу",
    };
}

/// <summary>
/// Часть составного поля справочника, несущая тэг (issue #1473): поле <paramref name="Whole" /> носителя,
/// его часть <paramref name="Key" /> и составной тип <paramref name="Type" />, в чьей схеме тэг стоит, —
/// там его и меняют.
/// </summary>
public sealed record RolePart(string Whole, string Key, DocumentType Type);

/// <summary>Поле справочника, найденное по тэгу.</summary>
/// <param name="Key">Ключ поля; <c>null</c>, пока <see cref="Fault" /> не <see cref="RoleFieldFault.None" />.</param>
/// <param name="Keys">Все ключи, несущие тэг у носителя роли, — для слов о нарушении.</param>
/// <param name="Subtype">Подтип, у которого тэг стоит иначе, — для <see cref="RoleFieldFault.DiffersInSubtype" />.</param>
/// <param name="Part">Тэг стоит на ЧАСТИ составного поля <see cref="Key" /> (issue #1473): значение
/// лежит в <c>Data[Key][Part.Key]</c>, а <see cref="Field" /> описывает часть. <c>null</c> — на самом поле.
/// Названа и при нарушении, если тэг стоит на части (в том числе «и на поле, и на части»): такое
/// нарушение снимается в редакторе составного типа, а не выбором поля справочника.
/// ⚠️ Кто читает значение по <see cref="Key" />, обязан учесть часть: иначе прочитал бы составное.
/// Не умеет — берёт <see cref="DirectKey" />.</param>
public sealed record ResolvedRoleField(
    string? Key, RoleFieldFault Fault, IReadOnlyList<string> Keys, SchemaFieldInfo? Field = null,
    DocumentType? Subtype = null, RolePart? Part = null)
{
    /// <summary>
    /// Ключ поля, когда значение лежит в нём ЦЕЛИКОМ; <c>null</c> — поле не найдено либо тэг стоит на
    /// части (ревью PR #1898). Для тех, кто читает поле справочника как есть — строку карточки, поле
    /// для записи при заведении, выбранное поле экрана сопоставления: им ключ составного означал бы
    /// чужое значение.
    /// </summary>
    public string? DirectKey => Part is null ? Key : null;

    /// <summary>
    /// Что не так — словами для человека; <c>null</c>, если поле найдено. ОДИН текст на всех, кто
    /// отказывает: чтение называло три причины из четырёх и не называло подтип, а заведение называло
    /// (ревью PR #1317) — человек проверял корневой тип, где всё было верно.
    /// </summary>
    public string? Describe(TagDefinition tag) => Fault switch
    {
        RoleFieldFault.None => null,
        RoleFieldFault.Several =>
            $"поле «{tag.Label}» назначено дважды ({string.Join(", ", Keys.Select(k => $"«{k}»"))}) — " +
            "какое из полей брать, решить нечем",
        RoleFieldFault.WrongKind =>
            $"поле «{tag.Label}» назначено полю «{Field!.Key}», а оно неподходящего вида либо расчётное",
        RoleFieldFault.DiffersInSubtype =>
            $"поле «{tag.Label}» у подтипа «{Subtype!.Name}» назначено другому полю, чем у самого справочника",
        _ => $"в справочнике не назначено поле «{tag.Label}»",
    };
}

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
    /// <summary>
    /// Может ли тип быть справочником: составной и лежит в общих данных. Документ записью справочника
    /// не бывает, а записи типа с собственной таблицей модуля общим путём не читаются и не заводятся.
    /// </summary>
    public static bool IsDirectory(DocumentType type) =>
        type.Kind == DocumentTypeKind.Composite && TypeStorageRules.KeptInCommonTable(type);

    public static ResolvedRole Role(string roleTag, IReadOnlyList<DocumentType> all)
    {
        // Носитель — тот, у кого тэг в СОБСТВЕННОЙ схеме: так же считает потолок MaxBearers. Подтип
        // роль наследует, но носителем не становится.
        var bearers = all.Where(t => SchemaTags.SchemaHasTypeTag(t.Schema, roleTag)).ToList();
        return new(roleTag, bearers,
            bearers.Count == 1 && IsDirectory(bearers[0]) ? Family(bearers[0], all) : new Dictionary<Guid, string>());
    }

    /// <summary>
    /// Роли, поставленные типу, который справочником быть не может, — отказ сохранению схемы.
    /// Сервер не сверяет тэг типа с его видом вообще (<c>AppliesTo</c> читает только редактор), а у
    /// роли цена ошибки — остановленный запуск.
    /// </summary>
    public static IEnumerable<string> UnfitBearerProblems(TagCatalog tags, DocumentType saving) =>
        IsDirectory(saving)
            ? []
            : tags.All
                .Where(t => tags.IsRole(t.Code) && SchemaTags.SchemaHasTypeTag(saving.Schema, t.Code))
                .Select(t =>
                    $"Тэг типа «{t.Label}» типу «{saving.Name}» поставить нельзя: справочником бывает только " +
                    "составной тип, записи которого лежат в общих данных.");

    /// <summary>
    /// Два справочника, которые делили бы записи, — отказ сохранению (ревью PR #1318).
    ///
    /// <para>Справочник — носитель роли со всеми потомками. Поэтому роли на одном типе и роли на типе и
    /// его предке означают одно: запись входит в оба справочника сразу. Потолок носителей этого не
    /// видит — он считает каждую роль отдельно.</para>
    ///
    /// <para>Дверей три, и проверка стоит на всех: тэг в схеме, новый тип под носителем и смена
    /// родителя. Отказывает только сохранению, которое в пересечении участвует: чужой тип правится и
    /// тогда, когда пересечение уже лежит в базе (его приносит восстановление копии).</para>
    /// </summary>
    /// <param name="saving">Сохраняемый тип — уже с входящей схемой и новым родителем.</param>
    public static IEnumerable<string> OverlapProblems(
        TagCatalog tags, DocumentType saving, IReadOnlyList<DocumentType> all)
    {
        var after = all.Where(t => t.Id != saving.Id).Append(saving).ToList();
        var byId = after.ToDictionary(t => t.Id);
        var bearers = tags.All.Where(t => tags.IsRole(t.Code))
            .SelectMany(role => after.Where(t => SchemaTags.SchemaHasTypeTag(t.Schema, role.Code))
                .Select(type => (Type: type, Role: role)))
            .ToList();

        foreach (var upper in bearers)
        foreach (var lower in bearers)
        {
            if (upper.Role.Code == lower.Role.Code) continue;

            if (upper.Type.Id == lower.Type.Id)
            {
                // Пара названа один раз, а не дважды в обе стороны.
                if (upper.Type.Id == saving.Id && string.CompareOrdinal(upper.Role.Code, lower.Role.Code) < 0)
                    yield return $"Тэги типа «{upper.Role.Label}» и «{lower.Role.Label}» не могут стоять на одном " +
                                 $"типе («{saving.Name}»): его записи вошли бы сразу в два справочника.";
                continue;
            }

            if (!IsAncestorOrSelf(upper.Type, lower.Type, byId)) continue;
            // Участвует ли сохраняемый: он один из двоих либо стоит в цепочке между ними.
            if (!IsAncestorOrSelf(saving, lower.Type, byId) || !IsAncestorOrSelf(upper.Type, saving, byId)) continue;

            yield return $"Тип «{lower.Type.Name}» с тэгом «{lower.Role.Label}» оказался бы подтипом " +
                         $"«{upper.Type.Name}» с тэгом «{upper.Role.Label}»: его записи вошли бы сразу в два справочника.";
        }
    }

    /// <summary>
    /// Стоит ли <paramref name="upper" /> в цепочке родителей <paramref name="lower" /> (или это он
    /// сам). Цикл в цепочке базой не запрещён — обход помнит пройденное.
    /// </summary>
    public static bool IsAncestorOrSelf(
        DocumentType upper, DocumentType lower, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var seen = new HashSet<Guid>();
        for (var cur = lower; cur is not null && seen.Add(cur.Id);
             cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            if (cur.Id == upper.Id) return true;

        return false;
    }

    public static ResolvedRoleField Field(ResolvedRole role, TagDefinition tag, IReadOnlyList<DocumentType> all)
    {
        if (role.Root is not { } root) return new(null, RoleFieldFault.Absent, []);

        var keys = KeysWithTag(root, all, tag.Code);
        var inParts = tag.PartOf is { } whole ? PartsWithTag(root, tag, whole, all) : null;

        // Тэг и на поле справочника, и на части его составного (ревью PR #1898): прямой молча победил
        // бы, а реестр обещает не выбирать молча (TagDefinition.PartOf). Нарушение — как два поля.
        if (keys.Count > 0 && inParts is { Parts.Count: > 0 } both)
            return new(null, RoleFieldFault.Several,
                [.. keys, .. both.Parts.Select(p => $"{both.Whole}.{p}")], Part: new(both.Whole, both.Parts[0], both.PartType));
        if (keys.Count == 0)
            return inParts is not null ? PartField(role, tag, inParts, all) : new(null, RoleFieldFault.Absent, keys);
        if (keys.Count > 1) return new(null, RoleFieldFault.Several, keys);

        var byId = all.ToDictionary(t => t.Id);
        var field = DocumentTypeSchemaReader.EffectiveFields(root.Id, byId).FirstOrDefault(f => f.Key == keys[0]);
        if (field is null) return new(null, RoleFieldFault.Absent, keys);

        // Вид сверяем с объявлением тэга: сервер при сохранении схемы его не проверяет, и поле
        // можно сделать составным, оставив тэг. Расчётное значения не хранит — читать в нём нечего.
        if (field.Computed || (tag.AppliesTo.Length > 0 && !tag.AppliesTo.Contains(field.Type, StringComparer.Ordinal)))
            return new(null, RoleFieldFault.WrongKind, keys, field);

        // Нарушение — тэг на ДРУГОМ поле подтипа. Подтип без тэга вовсе (исключил поле предка) —
        // не нарушение: читать у него нечего, а остальной справочник читается.
        foreach (var id in role.Family.Keys.Where(id => id != root.Id))
            if (KeysWithTag(byId[id], all, tag.Code).Any(k => k != keys[0]))
                return new(null, RoleFieldFault.DiffersInSubtype, keys, field, byId[id]);

        return new(keys[0], RoleFieldFault.None, keys, field);
    }

    /// <summary>Составное поле роли и части его типа, несущие тэг.</summary>
    private sealed record TaggedParts(string Whole, DocumentType PartType, List<string> Parts);

    /// <summary>
    /// Части с тэгом в составном поле <paramref name="whole" /> носителя (issue #1473); <c>null</c> —
    /// такого поля нет (не ровно одно у носителя, не составное, тип не найден).
    /// </summary>
    private static TaggedParts? PartsWithTag(
        DocumentType root, TagDefinition tag, string whole, IReadOnlyList<DocumentType> all)
    {
        var wholeKeys = KeysWithTag(root, all, whole);
        if (wholeKeys.Count != 1) return null;

        var byId = all.ToDictionary(t => t.Id);
        var field = DocumentTypeSchemaReader.EffectiveFields(root.Id, byId).FirstOrDefault(f => f.Key == wholeKeys[0]);
        return field is { Type: "complex", TypeId: { } partTypeId } && byId.GetValueOrDefault(partTypeId) is { } partType
            ? new(wholeKeys[0], partType, KeysWithTag(partType, all, tag.Code))
            : null;
    }

    /// <summary>
    /// Тэг на ЧАСТИ составного поля роли (issue #1473): у справочника тэга нет, а поле с тэгом
    /// <see cref="TagDefinition.PartOf" /> — составное, и в его типе тэг стоит на одной части. Поле
    /// названия берётся тем же расчётом, что и само (ровно одно у носителя); часть — ровно одна, того
    /// вида, что допускает тэг.
    /// </summary>
    private static ResolvedRoleField PartField(
        ResolvedRole role, TagDefinition tag, TaggedParts found, IReadOnlyList<DocumentType> all)
    {
        var (whole, partType, parts) = found;
        string[] wholeKeys = [whole];
        var byId = all.ToDictionary(t => t.Id);

        if (parts.Count == 0) return new(null, RoleFieldFault.Absent, []);
        if (parts.Count > 1)
            return new(null, RoleFieldFault.Several, [.. parts.Select(p => $"{whole}.{p}")], Part: new(whole, parts[0], partType));

        var part = DocumentTypeSchemaReader.EffectiveFields(partType.Id, byId).FirstOrDefault(f => f.Key == parts[0]);
        if (part is null) return new(null, RoleFieldFault.Absent, []);
        if (part.Computed || (tag.AppliesTo.Length > 0 && !tag.AppliesTo.Contains(part.Type, StringComparer.Ordinal)))
            return new(null, RoleFieldFault.WrongKind, wholeKeys, part, Part: new(whole, parts[0], partType));

        // Подтип, пометивший своё поле напрямую, читался бы не там — нарушение, как у обычного поля.
        foreach (var id in role.Family.Keys.Where(id => id != role.Root!.Id))
            if (KeysWithTag(byId[id], all, tag.Code).Count > 0)
                return new(null, RoleFieldFault.DiffersInSubtype, wholeKeys, part, byId[id], new(whole, parts[0], partType));

        return new(whole, RoleFieldFault.None, wholeKeys, part, Part: new(whole, parts[0], partType));
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
