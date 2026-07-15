using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Resolution;

/// <summary>Стратегия сопоставления строки с существующим объектом каталога (issue #183).</summary>
public enum ObjectMatchStrategy
{
    /// <summary>По конкретному полю данных (<see cref="ObjectMatchRequest.FieldKey"/> = <see cref="ObjectMatchRequest.Value"/>).</summary>
    Field,
    /// <summary>По имени объекта: DisplayName ∪ Aliases.</summary>
    Name,
    /// <summary>По составному ключу-идентификатору: конкатенация identity-полей типа (тэг «identity»)
    /// в порядке схемы. Значения полей строки передаются в <see cref="ObjectMatchRequest.Fields"/>.</summary>
    IdentityKey,
}

/// <summary>
/// Запрос сопоставления «строка→объект». Приоритет стратегий задаёт ВЫЗЫВАЮЩИЙ (одна стратегия на
/// запрос); резолвер политику не зашивает. Идентичность объекта — его GUID; ключи — только lookup.
/// </summary>
public sealed record ObjectMatchRequest
{
    /// <summary>Тип искомого объекта (составной). Кандидаты — этот тип и его подтипы.</summary>
    public required Guid TypeId { get; init; }
    public required ObjectMatchStrategy Strategy { get; init; }

    /// <summary>Field: значение колонки; Name: искомое имя/алиас. Для IdentityKey не используется.</summary>
    public string? Value { get; init; }

    /// <summary>Field: ключ поля данных, по которому идёт матч.</summary>
    public string? FieldKey { get; init; }

    /// <summary>IdentityKey: значения полей строки (fieldKey→value); резолвер сам берёт identity-поля
    /// типа в порядке схемы и строит из них составной ключ.</summary>
    public IReadOnlyDictionary<string, string?>? Fields { get; init; }

    public static ObjectMatchRequest ByField(Guid typeId, string fieldKey, string? value) =>
        new() { TypeId = typeId, Strategy = ObjectMatchStrategy.Field, FieldKey = fieldKey, Value = value };

    public static ObjectMatchRequest ByName(Guid typeId, string? value) =>
        new() { TypeId = typeId, Strategy = ObjectMatchStrategy.Name, Value = value };

    public static ObjectMatchRequest ByIdentity(Guid typeId, IReadOnlyDictionary<string, string?> fields) =>
        new() { TypeId = typeId, Strategy = ObjectMatchStrategy.IdentityKey, Fields = fields };
}

/// <summary>
/// Единый резолвер «строка→объект» (issue #183) для paste составных полей и источников данных.
/// Находит СУЩЕСТВУЮЩИЙ объект каталога (DomainObject, Facet==null) в скоп-поддереве владельца,
/// приоритет — узкий scope. **Строго read-only by contract**: не создаёт, не мутирует и не удаляет
/// объекты — создание/дедуп сюда не добавляется (это была бы отдельная write-операция с Admin-правами).
/// </summary>
public interface IObjectResolver
{
    /// <summary>Резолвит один запрос. null — совпадения нет (создание объектов не выполняется).</summary>
    Task<Guid?> ResolveAsync(ObjectMatchRequest req, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default);

    /// <summary>Батч в одном scope (кандидаты и скоп-цепочка строятся один раз). Порядок результата = порядок запросов.</summary>
    Task<IReadOnlyList<Guid?>> ResolveManyAsync(
        IReadOnlyList<ObjectMatchRequest> reqs, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default);
}
