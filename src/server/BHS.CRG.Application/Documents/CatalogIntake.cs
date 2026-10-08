using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Documents;

/// <summary>Что завести: тип, куда положить название и по какому полю запись единственна.</summary>
public sealed record CatalogIntakeRequest(
    Guid TypeId, string NameField, string UniqueField, string Name, string UniqueValue);

/// <summary>Исход — см. <c>ModuleIntakeResult</c> в контрактах модулей: заполнено одно из трёх.</summary>
public sealed record CatalogIntakeOutcome(
    DomainObject? Created, IReadOnlyList<CommonDataRef> Existing, IReadOnlyList<string> Refusals);

/// <summary>
/// Заведение записи справочника по названию и одному значению (issue #1077) — для порта модулей.
///
/// <para>Служба, а не команда: проверка «такой ещё нет» и создание обязаны идти в одной транзакции
/// под замком, а транзакцию открывает тот, кто владеет контекстом базы.</para>
/// </summary>
public interface ICatalogIntake
{
    Task<CatalogIntakeOutcome> CreateAsync(CatalogIntakeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Раскладка названия и значения по схеме типа — чистая функция (issue #1077).
///
/// <para>Модуль знает название строкой; как оно хранится, знает схема, а её ведёт человек. Поле
/// названия бывает строкой — и составным («Полное» и «Сокращённое»). В составное название ложится
/// в ОБЯЗАТЕЛЬНЫЕ строковые поля и только в них.</para>
///
/// <para>⚠️ Необязательные поля остаются пустыми, даже строковые и даже с тэгом идентичности.
/// «Сокращённое» название, заполненное полным из скана, — подставленное значение: его никто не
/// читал, а выглядит оно как прочитанное.</para>
/// </summary>
public static class CatalogIntakeLayout
{
    /// <summary>Данные новой записи либо причины, по которым её не собрать.</summary>
    public static (JsonDocument? Data, IReadOnlyList<string> Refusals) Build(
        CatalogIntakeRequest request, IReadOnlyDictionary<Guid, DocumentType> types)
    {
        var fields = DocumentTypeSchemaReader.EffectiveFields(request.TypeId, types);
        var refusals = new List<string>();
        var data = new JsonObject();

        if (fields.FirstOrDefault(f => f.Key == request.UniqueField) is { Computed: false } unique
            && SchemaFieldKinds.IsScalar(unique.Type))
            data[request.UniqueField] = request.UniqueValue;
        else
            refusals.Add($"в типе нет простого поля «{request.UniqueField}»");

        if (fields.FirstOrDefault(f => f.Key == request.NameField) is not { Computed: false } name)
            refusals.Add($"в типе нет поля «{request.NameField}»");
        else if (IsText(name.Type))
            data[request.NameField] = request.Name;
        else if (name is { Type: "complex", TypeId: { } inner })
            Nested(name, DocumentTypeSchemaReader.EffectiveFields(inner, types), request.Name, data, refusals);
        else
            refusals.Add($"поле «{Title(name)}» — не строка и не составное: название положить некуда");

        // Обязательные поля — по собранному. Охрана записи обязательность не проверяет (это правило
        // перехода, а не сохранения), поэтому без этой проверки легла бы запись, которую её же форма
        // сохранить не даст.
        if (refusals.Count == 0)
        {
            var built = JsonSerializer.SerializeToElement(data);
            var missing = RequiredFields.Missing(fields,
                key => built.TryGetProperty(key, out var value) ? value : null);
            if (missing.Count > 0)
                refusals.Add("обязательные поля, которые из скана заполнить нечем: "
                    + string.Join(", ", missing.Select(f => $"«{Title(f)}»")));
        }

        return refusals.Count > 0
            ? (null, refusals)
            : (JsonSerializer.SerializeToDocument(data), refusals);
    }

    private static void Nested(
        SchemaFieldInfo field, IReadOnlyList<SchemaFieldInfo> inner, string name, JsonObject data, List<string> refusals)
    {
        var required = inner.Where(f => f.Required && !f.Computed).ToList();
        var foreign = required.Where(f => !IsText(f.Type)).ToList();
        if (foreign.Count > 0)
        {
            refusals.Add($"в поле «{Title(field)}» обязательны не только строки: "
                + string.Join(", ", foreign.Select(f => $"«{Title(f)}»")));
            return;
        }

        if (required.Count == 0)
        {
            refusals.Add($"в поле «{Title(field)}» нет обязательной строки: в какую из его частей класть название, по схеме не понять");
            return;
        }

        var node = new JsonObject();
        foreach (var part in required) node[part.Key] = name;
        data[field.Key] = node;
    }

    private static bool IsText(string type) => type is "string" or "text";

    private static string Title(SchemaFieldInfo field) => string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title;
}
