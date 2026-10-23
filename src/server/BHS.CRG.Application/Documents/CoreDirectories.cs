using System.Globalization;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Справочники ядра, которые код читает ПО СМЫСЛУ — вид работы, единица измерения, сотрудник
/// (ТЗ CORE-20.4, PLAN-6.1, PLAN-16): их находят дверь видов работ и порты модулей.
///
/// <para><b>Справочник — по коду, поле — по тэгу или цели, а не по ключу.</b> Код типа постоянен, а
/// ключи полей заказчик вправе переименовать (уровень «расширяемый», CORE-19): единица, прочитанная
/// по ключу «ЕдиницаИзмерения», после переименования поля молча пропала бы из позиции перечня.</para>
/// </summary>
public static class CoreDirectories
{
    /// <summary>
    /// Тип справочника по коду и его подтипы — запись подтипа остаётся записью справочника, как у
    /// ролей (<see cref="RoleResolution" />). <c>null</c> — справочника на экземпляре нет: «не заведён»
    /// и «записей нет» — разные ответы, и выбирает между ними звавший.
    /// </summary>
    public static IReadOnlyDictionary<Guid, DocumentType>? Family(IReadOnlyList<DocumentType> all, string code)
    {
        var root = all.FirstOrDefault(t => t.Code == code);
        if (root is null) return null;
        var byId = all.ToDictionary(t => t.Id);
        return all.Where(t => DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, root.Id, byId))
            .ToDictionary(t => t.Id);
    }

    /// <summary>Строковое значение поля; пустое и отсутствующее — <c>null</c>. Число — его записью.</summary>
    public static string? Text(JsonElement data, string? key) =>
        Value(data, key) switch
        {
            { ValueKind: JsonValueKind.String } s when !string.IsNullOrWhiteSpace(s.GetString()) => s.GetString()!.Trim(),
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            _ => null,
        };

    /// <summary>Признак: <c>true</c> только у настоящего <c>true</c> — незаполненный признак не поставлен.</summary>
    public static bool Flag(JsonElement data, string? key) => Value(data, key) is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// Дата поля (<c>yyyy-MM-dd</c>, с временем или без). Негодная запись — <c>null</c>, как и пустая:
    /// дату в форму вводит человек, и отказ всего ответа из-за одной карточки закрыл бы модулю весь
    /// справочник.
    /// </summary>
    public static DateOnly? Date(JsonElement data, string? key) =>
        Text(data, key) is { Length: >= 10 } text
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// Запись, на которую ссылается поле: <c>{"$ref":"catalog","entryId":…}</c>. Значение, вписанное
    /// в поле объектом без ссылки, записи не называет — <c>null</c>.
    /// </summary>
    public static Guid? Reference(JsonElement data, string? key) =>
        Value(data, key) is { } v ? CatalogRefs.EntryId(v) : null;

    /// <summary>
    /// Ключ первого поля с тэгом — в эффективном составе типа: тэги ближнего типа побеждают, а поле,
    /// которое подтип исключил (<c>excludedFields</c>), не находится — его у типа нет, как нет в форме.
    /// </summary>
    public static string? TaggedKey(DocumentType type, IReadOnlyList<DocumentType> all, string tag) =>
        SchemaTags.TaggedFieldsInSchemaOrder(type, all).Where(f => f.Tag.Code == tag).Select(f => f.Key).FirstOrDefault();

    private static JsonElement? Value(JsonElement data, string? key) =>
        key is not null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value)
            ? value
            : null;
}

/// <summary>
/// Где у вида работы лежат его части (ТЗ CORE-8, PLAN-6.1): код и наименование — компоненты ключа
/// идентичности (<c>identity:1</c>, <c>identity:2</c>), единица — поле, ЦЕЛЬ которого — справочник
/// единиц, признаки — по тэгам <c>work.*</c>. <c>null</c> у части — в схеме её нет.
/// </summary>
public sealed record WorkTypeKeys(
    string? Code, string? Name, string? Unit, string? RequiresLocation, string? RequiresMaterials, string? ProducesId)
{
    /// <param name="unitTypes">Тип единиц и его подтипы: цель поля сверяется с ними.</param>
    public static WorkTypeKeys Of(DocumentType type, IReadOnlyList<DocumentType> all, IReadOnlyCollection<Guid> unitTypes)
    {
        var identity = SchemaTags.OrderedKeysWithTag(type, all, FunctionalTag.Identity);
        // Состав с тэгами — один на три признака: каждый вызов TaggedKey разбирал бы цепочку заново.
        var tagged = SchemaTags.TaggedFieldsInSchemaOrder(type, all);
        string? Tagged(string tag) => tagged.Where(f => f.Tag.Code == tag).Select(f => f.Key).FirstOrDefault();
        // Единица — по цели, а не по тэгу: тэга у поля нет, а цель ядро объявило само и заказчик её не
        // меняет (поле от ядра). По ключу искать нельзя — его переименуют.
        var unit = DocumentTypeSchemaReader.EffectiveFields(type.Id, all.ToDictionary(t => t.Id))
            .FirstOrDefault(f => DocumentTypeSchemaReader.IsSingleComposite(f.Type)
                && f.TypeId is { } target && unitTypes.Contains(target))?.Key;
        return new(
            identity.ElementAtOrDefault(0), identity.ElementAtOrDefault(1), unit,
            Tagged(FunctionalTag.WorkRequiresLocation), Tagged(FunctionalTag.WorkRequiresMaterials),
            Tagged(FunctionalTag.WorkProducesId));
    }
}
