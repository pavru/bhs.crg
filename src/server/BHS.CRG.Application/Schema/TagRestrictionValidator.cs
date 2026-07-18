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
        JsonDocument incomingSchema, Guid savingTypeId, string savingTypeName,
        IReadOnlyList<DocumentType> allDocTypes)
    {
        var violations = new List<TagRestrictionViolation>();
        foreach (var def in TagRegistry.All)
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
