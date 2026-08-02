using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Соответствие значений объявленному типу поля (issue #461).
///
/// Повод: поле «ПорядковыйНомер» объявлено примитивом «Цело число», а хранит иерархическую нумерацию
/// «2.1», «3.4», «10». Восемнадцать значений строки, одно — дробное число; <c>validate_document</c>
/// возвращал ноль ошибок и ноль предупреждений, потому что соответствие типу не проверялось вовсе.
///
/// Сами правила листа живут в <see cref="ValueTypeRules"/> — их разделяет аудит типа (issue #642).
/// Здесь остаётся ОБХОД: по разрешённому контексту генерации, вглубь составных полей и строк таблиц.
///
/// Всё выдаётся ПРЕДУПРЕЖДЕНИЯМИ: данные накоплены, и половина документов перестала бы выпускаться в
/// тот же день. Задача — показать расхождение, а не заблокировать работу; что чинить — объявление
/// типа или данные — решает человек.
/// </summary>
public static class ValueTypeScanner
{
    /// <summary>Глубина обхода составных полей: защита от патологически вложенных схем.</summary>
    private const int MaxDepth = 6;

    public static void Scan(
        GenerationContext ctx,
        IReadOnlyList<SchemaFieldInfo> effectiveFields,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics)
    {
        foreach (var f in effectiveFields)
        {
            if (!ctx.Data.TryGetValue(f.Key, out var raw) || raw is not JsonElement value) continue;
            Check(f.Key, f, value, typesById, primitivesById, diagnostics, depth: 0);
        }
    }

    private static void Check(
        string path, SchemaFieldInfo field, JsonElement value,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics, int depth)
    {
        // Расчётное поле производное: претензия к его значению — претензия к выражению (#368).
        if (field.Computed) return;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        if (depth >= MaxDepth) return;

        switch (field.Type)
        {
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    Warn(diagnostics, path, $"Поле «{Title(field)}» — таблица, а хранится одиночное значение.");
                    return;
                }
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    CheckComposite($"{path}[{i++}]", field, item, typesById, primitivesById, diagnostics, depth);
                return;

            case "complex":
                CheckComposite(path, field, value, typesById, primitivesById, diagnostics, depth);
                return;

            // Ссылки, файлы и картинки проверяет резолвер ссылок; здесь их трогать нечем.
            case "doc-ref" or "doc-array" or "file" or "image":
                return;

            default:
                foreach (var message in ValueTypeRules.CheckScalar(field, value, primitivesById))
                    Warn(diagnostics, path, message);
                return;
        }
    }

    private static void CheckComposite(
        string path, SchemaFieldInfo field, JsonElement value,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            Warn(diagnostics, path, $"Поле «{Title(field)}» — составное, а хранится простое значение.");
            return;
        }
        if (field.TypeId is not { } typeId || !typesById.ContainsKey(typeId)) return;

        // Именно здесь и живёт находка: верхнего уровня недостаточно, «ПорядковыйНомер» лежит внутри
        // элементов массива «Работы».
        foreach (var inner in DocumentTypeSchemaReader.EffectiveFields(typeId, typesById))
        {
            if (!value.TryGetProperty(inner.Key, out var innerValue)) continue;
            Check($"{path}.{inner.Key}", inner, innerValue, typesById, primitivesById, diagnostics, depth + 1);
        }
    }

    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;

    private static void Warn(List<ResolutionDiagnostic> diagnostics, string path, string message)
        => diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, path, message, "value-type"));
}
