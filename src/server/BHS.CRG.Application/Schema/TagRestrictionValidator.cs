using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>Носитель тэга: тип (Type-scope) или пара тип+поле (Field-scope).</summary>
public record TagBearer(Guid TypeId, string TypeName, string? FieldKey);

/// <summary>Нарушение ограничения тэга — где тэг уже используется сверх лимита.</summary>
public record TagRestrictionViolation(string TagCode, string TagLabel, int MaxBearers, IReadOnlyList<TagBearer> Bearers)
{
    /// <summary>Человекочитаемое сообщение со списком занятых мест (для 409).</summary>
    public string Describe()
    {
        var places = string.Join(", ", Bearers.Select(b =>
            b.FieldKey is null ? $"«{b.TypeName}»" : $"«{b.TypeName}».{b.FieldKey}"));
        return $"Тэг «{TagLabel}» допускает не более {MaxBearers} носител{(MaxBearers == 1 ? "я" : "ей")} " +
               $"во всей системе — уже используется: {places}.";
    }
}

/// <summary>
/// Нарушение КРАТНОСТИ тэга внутри одного типа (ТЗ TYPE-21, столбец «Сколько»; issue #959):
/// одиночный тэг стоит на нескольких полях типа.
/// </summary>
/// <param name="TypeName">
/// Чей это тип. Нарушение бывает и НЕ у сохраняемого — правка схемы предка создаёт второго
/// носителя у потомка, — и тогда без имени типа сообщение называет поля, которых в открытой форме
/// нет вовсе.
/// </param>
public record TagCardinalityViolation(
    string TagCode, string TagLabel, string TypeName, IReadOnlyList<string> FieldKeys)
{
    public string Describe() =>
        $"Тэг «{TagLabel}» ставится не более чем одному полю типа — в типе «{TypeName}» он стоит " +
        $"у полей: {string.Join(", ", FieldKeys)}. Код находит поле по тэгу и взял бы первое " +
        "попавшееся из них.";
}

/// <summary>
/// Проверка внутренних ограничений тэгов (issue #258) при сохранении схемы типа. Чистая функция без I/O:
/// считает РАЗЛИЧНЫХ носителей restricted-тэга по СОБСТВЕННЫМ схемам среди прочих типов + входящей схемы;
/// превышение <see cref="TagRestriction.MaxBearers"/> → нарушение. Носитель — тип (Type-scope) или
/// (тип, ключ поля) (Field-scope). Наследованные тэги НЕ считаются (только own-схема) — иначе первый же
/// подтип «унаследовал» бы тэг и сломал лимит. Вызывается из Create и UpdateSchema (обе точки несут схему).
/// </summary>
public static class TagRestrictionValidator
{
    /// <param name="savingTypeId">Id сохраняемого типа; при создании — <c>Guid.Empty</c> (не совпадёт ни с одним типом).</param>
    public static IReadOnlyList<TagRestrictionViolation> Validate(
        TagCatalog catalog, JsonDocument incomingSchema, Guid savingTypeId, string savingTypeName,
        IReadOnlyList<DocumentType> allDocTypes)
    {
        var violations = new List<TagRestrictionViolation>();
        foreach (var def in catalog.All)
        {
            if (def.Restriction?.MaxBearers is not { } max) continue;
            // Механизм по каталогу типов покрывает Type/Field. Dataset/GostDocument живут на других
            // сущностях — их restriction (если появится) валидируется отдельным энумератором.
            if (def.Scope is not (TagScope.Type or TagScope.Field)) continue;

            var bearers = new List<TagBearer>();
            foreach (var t in allDocTypes)
            {
                if (t.Id == savingTypeId) continue; // сохраняемый тип берём из входящей схемы, не из старой
                bearers.AddRange(BearersOf(def, t.Id, t.Name, t.Schema));
            }
            bearers.AddRange(BearersOf(def, savingTypeId, savingTypeName, incomingSchema));

            var distinct = bearers
                .GroupBy(b => (b.TypeId, b.FieldKey))
                .Select(g => g.First())
                .ToList();
            if (distinct.Count > max)
                violations.Add(new(def.Code, def.Label, max, distinct));
        }
        return violations;
    }

    private static IEnumerable<TagBearer> BearersOf(TagDefinition def, Guid typeId, string typeName, JsonDocument schema)
    {
        if (def.Scope == TagScope.Type)
        {
            if (SchemaTags.SchemaHasTypeTag(schema, def.Code))
                yield return new(typeId, typeName, null);
        }
        else // Field
        {
            foreach (var key in SchemaTags.FieldKeysWithTag(schema, def.Code))
                yield return new(typeId, typeName, key);
        }
    }
}

/// <summary>
/// Кратность тэга внутри одного типа (ТЗ TYPE-21, столбец «Сколько»; issue #959): тэг с
/// <c>Multiple: false</c> ставится не более чем одному полю типа.
///
/// <para>Ограничение было ОБЪЯВЛЕНО с самого появления реестра и не проверялось нигде — ни на
/// сервере, ни в редакторе. Цена тишины видна на любом читателе: код ищет поле по тэгу и берёт
/// первое попавшееся, то есть из двух помеченных полей молча выбирает одно, а какое — зависит от
/// порядка в схеме.</para>
///
/// <para>Считается по ЭФФЕКТИВНОМУ набору тэгов (<see cref="SchemaTags.TaggedFieldsInSchemaOrder" />),
/// а не по собственной схеме: читатель видит поля вместе с унаследованными, и тэг, добавленный
/// потомком рядом с унаследованным, даёт ровно ту же двусмысленность. По этой же причине набор
/// берётся с учётом <c>excludedFields</c> — потомок вправе ИСКЛЮЧИТЬ помеченное поле предка и
/// пометить своё, и запрещать ему это было бы запретом на законное действие.</para>
///
/// <para>⚠️ Тэг, которого нет в реестре этого экземпляра (модуль-владелец выключен), не
/// проверяется и не считается нарушением: его кратность объявлена в коде, которого здесь нет. Он
/// просто остаётся в схеме — выключение модуля не повод отказывать в сохранении чужой схемы.</para>
///
/// <para><b>Где проверка стоит.</b> Везде, где меняется состав тэгов типа ИЛИ его цепочка
/// наследования: заведение типа, сохранение схемы, смена родителя и проекция объявления модуля.
/// Перечень полный по состоянию на issue #959; остальные места, правящие схему, — миграция
/// адресации блоков (#773) и снятие размеров картинки — тэгов не добавляют вовсе, а восстановление
/// из копии проверкой и не должно закрываться: правило, введённое позже архива, не повод не дать
/// его развернуть.</para>
/// </summary>
public static class TagCardinalityValidator
{
    /// <param name="saving">
    /// Сохраняемый тип, УЖЕ несущий входящую схему (<c>DocumentType.WithSchema</c> при правке,
    /// свежесозданный при заведении). Именно тип, а не схема: эффективный набор тэгов считается по
    /// цепочке наследования, а её задаёт тип.
    /// </param>
    public static IReadOnlyList<TagCardinalityViolation> Validate(
        TagCatalog catalog, DocumentType saving, IReadOnlyList<DocumentType> allDocTypes)
    {
        // Каталог типов, каким он СТАНЕТ: сохраняемый тип подменён входящей схемой, новый —
        // добавлен. Без подмены потомки считались бы по старой схеме предка, то есть по тому, что
        // как раз и правят.
        var effective = allDocTypes.Where(t => t.Id != saving.Id).Append(saving).ToList();

        // Проверяем сохраняемый тип И ЕГО ПОТОМКОВ. Наверх смотреть мало: правка схемы ПРЕДКА
        // добавляет носителя потомку, и без этого обхода она проходила бы молча — а потомок после
        // неё не сохранялся бы уже никогда, отказом про поля, которых в его схеме нет (поймано
        // ревью PR #1012).
        var violations = new List<TagCardinalityViolation>();
        foreach (var type in effective.Where(t => t.Id == saving.Id || IsDescendantOf(t, saving.Id, effective)))
            violations.AddRange(ViolationsIn(catalog, type, effective));
        return violations;
    }

    private static IEnumerable<TagCardinalityViolation> ViolationsIn(
        TagCatalog catalog, DocumentType type, IReadOnlyList<DocumentType> effective)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, tag) in SchemaTags.TaggedFieldsInSchemaOrder(type, effective))
        {
            if (!byKey.TryGetValue(tag.Code, out var keys)) byKey[tag.Code] = keys = [];
            // Одно поле, помеченное тэгом дважды, кратности не нарушает: носитель один.
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }

        foreach (var (code, keys) in byKey)
        {
            if (keys.Count < 2) continue;
            // Тэг вне реестра — чужой или выключённого модуля: его правил здесь не знают.
            if (catalog.Find(code) is not { Multiple: false } def) continue;
            yield return new(def.Code, def.Label, type.Name, keys);
        }
    }

    /// <summary>Потомок ли тип — по цепочке родителей, со страховкой от цикла.</summary>
    private static bool IsDescendantOf(DocumentType type, Guid ancestorId, IReadOnlyList<DocumentType> all)
    {
        var visited = new HashSet<Guid>();
        var current = type.ParentId;
        while (current is { } id && visited.Add(id))
        {
            if (id == ancestorId) return true;
            current = all.FirstOrDefault(t => t.Id == id)?.ParentId;
        }
        return false;
    }
}
