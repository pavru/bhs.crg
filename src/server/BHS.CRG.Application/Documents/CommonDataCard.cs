using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>Чем значение строки карточки было в записи — чтобы показ отличал дату от текста.</summary>
public enum CardLineKind
{
    Text = 1,
    Number = 2,
    Date = 3,
    Boolean = 4,

    /// <summary>Перечисление: в тексте — подпись значения, а не код.</summary>
    Choice = 5,

    /// <summary>Ссылка на другую запись: в тексте — её название.</summary>
    Reference = 6,
}

/// <summary>Строка карточки — поле записи, как его прочтёт человек.</summary>
/// <param name="Key">Ключ поля в схеме — по нему звавший находит размеченные тэгами поля.</param>
/// <param name="Text">Значение для показа; <c>null</c> — не заполнено (или не прочитано, см.
/// <paramref name="Unreadable" />).</param>
/// <param name="Identity">Поле ключа идентичности: такие стоят первыми и всегда, даже пустые.</param>
/// <param name="InheritedFrom">Название основы (<c>_baseRef</c>), от которой значение пришло; <c>null</c> — своё.</param>
/// <param name="Unreadable">Значение прочитать НЕ УДАЛОСЬ: основа удалена или вне области, ссылка ведёт в
/// никуда, в поле лежит не то, что объявлено. Это не «пусто».</param>
/// <param name="Archived">Ссылка ведёт на архивную запись.</param>
public sealed record CommonDataCardLine(
    string Key, string Title, string? Text, CardLineKind Kind, bool Identity, string? InheritedFrom,
    bool Unreadable, bool Archived = false);

/// <summary>
/// Карточка записи общих данных — ПРЕДСТАВЛЕНИЕ ДЛЯ ЧЕЛОВЕКА (issue #1516): поля по схеме вида записи, с
/// разрешённым наследованием и названиями ссылок.
///
/// <para>⚠️ <b>Сравнивать и сопоставлять по этим значениям нельзя.</b> Текст здесь — подпись: перечисление
/// названо подписью, да/нет — словом, ссылка — названием цели. Кому нужно значение, спрашивает
/// <see cref="CommonDataFieldValuesQuery" />; иначе карточка станет третьим способом читать справочник, и он
/// разойдётся с первыми двумя на первом же переименованном варианте перечисления.</para>
/// </summary>
/// <param name="TypeName">Название вида записи.</param>
/// <param name="NotShown">Заполненные поля, содержимое которых карточка не показывает НИКОГДА, — картинки,
/// файлы, составные и табличные: заголовком, у списков — с числом («Сертификаты (2)»).</param>
/// <param name="EmptyCount">Сколько простых полей вне ключа не заполнено — их в строках нет.</param>
/// <param name="Scope">Уровень области записи.</param>
public sealed record CommonDataCard(
    CommonDataRef Record, string TypeName, IReadOnlyList<CommonDataCardLine> Lines, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> NotShown, int EmptyCount, CatalogScope Scope);

/// <summary>
/// Карточка записи общих данных одного из названных видов; <c>null</c> — записи нет, она другого вида или
/// это документ. Виды называет звавший: адрес по идентификатору иначе открыл бы любую запись ядра.
/// </summary>
public sealed record CommonDataCardQuery(Guid Id, IReadOnlyCollection<Guid> TypeIds) : IRequest<CommonDataCard?>;

/// <summary>Одно звено цепочки наследования записи: она сама, затем её основы по порядку.</summary>
public sealed record CardSource(
    Guid Id, Guid CompositeTypeId, string? DisplayName, bool Archived, CatalogScope ScopeLevel, Guid? ScopeId,
    bool IsDocument, IReadOnlyList<string> Aliases, JsonElement Data);

/// <summary>
/// Запись и ДОСТИЖИМЫЕ основы — тем же правилом, что сопоставление по реквизиту и печать: основа удалена,
/// лежит на чужой ветке областей или цепочка замкнулась — дальше не читается.
/// </summary>
/// <param name="Broken">Последнее звено называет основу, до которой дойти нельзя: поле, которого нет в
/// звеньях, — «не прочитано», а не «нет».</param>
public sealed record CardChain(IReadOnlyList<CardSource> Links, bool Broken);

public sealed class CommonDataCardHandler(
    IDomainObjectRepository objects, IRepository<DocumentType> types, IRepository<EnumType> enums)
    : IRequestHandler<CommonDataCardQuery, CommonDataCard?>
{
    private const string BaseRef = "_baseRef";

    public async Task<CommonDataCard?> Handle(CommonDataCardQuery q, CancellationToken ct)
    {
        if (await objects.CardChainAsync(q.Id, ct) is not { } chain) return null;
        var record = chain.Links[0];
        if (record.IsDocument || !q.TypeIds.Contains(record.CompositeTypeId)) return null;

        var all = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);
        if (all.GetValueOrDefault(record.CompositeTypeId) is not { } type) return null;
        var fields = DocumentTypeSchemaReader.EffectiveFields(type.Id, all, (await enums.GetAllAsync(ct)).ToDictionary(e => e.Id));
        var identity = SchemaTags.OrderedKeysWithTag(type, [.. all.Values], FunctionalTag.Identity).ToHashSet(StringComparer.Ordinal);

        var resolved = fields.Where(f => !f.Computed).Select(f => (Field: f, Value: Resolve(chain, f.Key))).ToList();
        var targets = await TargetsAsync(resolved.Select(r => r.Value.Value), ct);

        var lines = new List<CommonDataCardLine>();
        var notShown = new List<string>();
        var empty = 0;
        // Поля ключа первыми, оба списка — в порядке схемы.
        foreach (var (field, value) in resolved.OrderBy(r => identity.Contains(r.Field.Key) ? 0 : 1))
        {
            var title = string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title;
            var key = identity.Contains(field.Key);
            var from = value.From?.DisplayName is { } named ? named : value.From is null ? null : "без названия";

            if (value.Unreadable)
            {
                lines.Add(new(field.Key, title, null, Kind(field), key, null, Unreadable: true));
                continue;
            }

            if (IsEmpty(value.Value))
            {
                if (key) lines.Add(new(field.Key, title, null, Kind(field), key, from, Unreadable: false));
                else empty++;
                continue;
            }

            var element = value.Value!.Value;
            if (Hidden(field, element, title) is { } caption)
            {
                notShown.Add(caption);
                continue;
            }

            if (Reference(element) is { } target)
            {
                var found = targets.GetValueOrDefault(target);
                // Цель удалена — ссылка есть, записи нет: это не «пусто».
                lines.Add(new(field.Key, title, found?.DisplayName, CardLineKind.Reference, key, from,
                    Unreadable: found is null, Archived: found?.Archived ?? false));
                continue;
            }

            var text = Text(field, element);
            lines.Add(new(field.Key, title, text, Kind(field), key, from, Unreadable: text is null));
        }

        return new CommonDataCard(
            new CommonDataRef(record.Id, record.CompositeTypeId, record.DisplayName, record.Archived),
            type.Name, lines, record.Aliases, notShown, empty, record.ScopeLevel);
    }

    /// <summary>
    /// Значение поля по цепочке: первое звено, где ключ ЕСТЬ, — даже пустой (своё перекрывает основу, как у
    /// слияния печати, <c>BaseRefReader.MergeObjects</c>); не нашлось, а цепочка оборвана — не прочитано.
    /// </summary>
    private static (JsonElement? Value, CardSource? From, bool Unreadable) Resolve(CardChain chain, string key)
    {
        if (key == BaseRef) return (null, null, false);
        for (var i = 0; i < chain.Links.Count; i++)
            if (chain.Links[i].Data.ValueKind == JsonValueKind.Object && chain.Links[i].Data.TryGetProperty(key, out var value))
                return (value, i == 0 ? null : chain.Links[i], false);
        return (null, null, chain.Broken);
    }

    /// <summary>Названия записей, на которые ссылаются поля, — одним обращением.</summary>
    private async Task<Dictionary<Guid, CommonDataRef>> TargetsAsync(IEnumerable<JsonElement?> values, CancellationToken ct)
    {
        var ids = values.Select(Reference).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? [] : (await objects.RefsAmongAsync(ids, ct)).ToDictionary(r => r.Id);
    }

    private static bool IsEmpty(JsonElement? value) => value is not { } v || v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        JsonValueKind.Object => !v.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>
    /// Подпись поля, содержимое которого не показывается никогда: картинка и файл — это base64, составное и
    /// таблица — не одно значение. <c>null</c> — поле показывается строкой.
    /// </summary>
    private static string? Hidden(SchemaFieldInfo field, JsonElement value, string title) => field.Type switch
    {
        "image" or "file" => title,
        _ when DocumentTypeSchemaReader.IsMultiValued(field.Type) || value.ValueKind == JsonValueKind.Array =>
            value.ValueKind == JsonValueKind.Array ? $"{title} ({value.GetArrayLength()})" : title,
        // Составное ссылкой на запись — строка с её названием; вложенный объект — не показывается.
        _ when DocumentTypeSchemaReader.IsSingleComposite(field.Type) || value.ValueKind == JsonValueKind.Object =>
            Reference(value) is null ? title : null,
        _ => null,
    };

    /// <summary>Запись, на которую ссылается значение: <c>{"$ref":"catalog","entryId":…}</c>.</summary>
    private static Guid? Reference(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } v
        && v.TryGetProperty("$ref", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "catalog"
        && v.TryGetProperty("entryId", out var id) && id.ValueKind == JsonValueKind.String
        && Guid.TryParse(id.GetString(), out var entry)
            ? entry
            : null;

    private static CardLineKind Kind(SchemaFieldInfo field) => field.Type switch
    {
        "number" or "integer" => CardLineKind.Number,
        "date" or "datetime" => CardLineKind.Date,
        "boolean" => CardLineKind.Boolean,
        "enum" => CardLineKind.Choice,
        _ when DocumentTypeSchemaReader.IsSingleComposite(field.Type) => CardLineKind.Reference,
        _ => CardLineKind.Text,
    };

    /// <summary>Значение простого поля текстом; <c>null</c> — в поле лежит не скаляр.</summary>
    private static string? Text(SchemaFieldInfo field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "да",
        JsonValueKind.False => "нет",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String when field.Type == "enum" =>
            field.Options?.FirstOrDefault(o => o.Code == value.GetString())?.Label ?? value.GetString(),
        JsonValueKind.String when field.Type == "boolean" =>
            bool.TryParse(value.GetString(), out var flag) ? (flag ? "да" : "нет") : value.GetString(),
        JsonValueKind.String => value.GetString(),
        _ => null,
    };
}
