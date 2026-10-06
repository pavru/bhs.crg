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

    /// <summary>
    /// Тот же вопрос, но от ДАННЫХ записи: «есть ли уже запись с таким ключом идентичности?»
    /// (issue #1185). Скаляры верхнего уровня читаются так же, как резолвер читает их у кандидатов, —
    /// иначе число <c>7</c> в новой записи не совпало бы с числом <c>7</c> в лежащей.
    /// </summary>
    public static ObjectMatchRequest ByIdentityOf(Guid typeId, System.Text.Json.JsonElement data)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in data.EnumerateObject())
                fields[p.Name] = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number => p.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    _ => null,
                };
        return ByIdentity(typeId, fields);
    }
}

/// <summary>
/// Найденная запись и её состояние (issue #1185). Не голый идентификатор нарочно: резолвер находит
/// и архивные записи, а подставлять ли такую — решает звавший, и по одному идентификатору он бы
/// этого не узнал. Тип ответа сменён, чтобы каждый потребитель решил это на компиляции.
/// </summary>
/// <param name="Archived">Запись в архиве: совпала, но на новый выбор не годится.</param>
public readonly record struct ObjectMatch(Guid Id, bool Archived);

/// <summary>
/// Единый резолвер «строка→объект» (issue #183) для paste составных полей и источников данных.
/// Находит СУЩЕСТВУЮЩИЙ объект каталога (DomainObject, Facet==null) в скоп-поддереве владельца,
/// приоритет — узкий scope. **Строго read-only by contract**: не создаёт, не мутирует и не удаляет
/// объекты — создание/дедуп сюда не добавляется (это была бы отдельная write-операция с Admin-правами).
///
/// <para><b>Архивные записи находит, но действующая побеждает</b> (ТЗ CORE-34.4, issue #1185):
/// сначала состояние, потом уровень. Иначе архивная запись комплекта заслонила бы действующую
/// системную с тем же ключом — и строка, у которой есть законная цель, осталась бы без ссылки.
/// Скрывать архивные вовсе нельзя: «не найдено» отправило бы человека заводить дубль записи,
/// лежащей в архиве.</para>
/// </summary>
public interface IObjectResolver
{
    /// <summary>Резолвит один запрос. null — совпадения нет (создание объектов не выполняется).</summary>
    Task<ObjectMatch?> ResolveAsync(ObjectMatchRequest req, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default);

    /// <summary>Батч в одном scope (кандидаты и скоп-цепочка строятся один раз). Порядок результата = порядок запросов.</summary>
    /// <summary>
    /// Тот же вопрос, но по базе КАК ОНА ЕСТЬ СЕЙЧАС и без следа в памяти резолвера (issue #1185).
    /// Для вопроса ПЕРЕД записью («нет ли уже такой?»): кандидаты, запомненные до записи, после неё
    /// устарели бы, и всё, что в той же области служб резолвит дальше, новой записи не увидело бы.
    /// </summary>
    Task<ObjectMatch?> ResolveFreshAsync(ObjectMatchRequest req, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default);

    Task<IReadOnlyList<ObjectMatch?>> ResolveManyAsync(
        IReadOnlyList<ObjectMatchRequest> reqs, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default);
}
