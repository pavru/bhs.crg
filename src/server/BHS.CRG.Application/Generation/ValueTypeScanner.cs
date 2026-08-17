using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

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
        if (field.TypeId is not { } typeId || !typesById.TryGetValue(typeId, out var composite)) return;

        var inners = DocumentTypeSchemaReader.EffectiveFields(typeId, typesById);
        CheckUnionArity(path, composite, inners, value, typesById, diagnostics);

        // Именно здесь и живёт находка: верхнего уровня недостаточно, «ПорядковыйНомер» лежит внутри
        // элементов массива «Работы».
        foreach (var inner in inners)
        {
            if (!value.TryGetProperty(inner.Key, out var innerValue)) continue;
            Check($"{path}.{inner.Key}", inner, innerValue, typesById, primitivesById, diagnostics, depth + 1);
        }
    }

    /// <summary>
    /// Инвариант union'а — «заполнен ровно один вариант» (issue #320, проверка — #756).
    ///
    /// <para>Записать такое значение приложение не даёт: пикер варианта хранит один ключ, а таблицу,
    /// где колонок было по числу вариантов, закрыла #748. Но <c>PUT …/requisites</c> кладёт тело как
    /// есть — путь записи схема-агностичен СОЗНАТЕЛЬНО, — так что строка с двумя ключами приезжает из
    /// восстановленной копии, правки JSONB руками или импорта. Здесь мы её и ловим: диагностика
    /// генерации — единственное место, которое видит значения целиком и не мешает их записывать.</para>
    ///
    /// <para>Предупреждение, не ошибка, — как и всё в этом сканере: данные уже накоплены, а решение
    /// «какой вариант верный» человеческое. Молчать при этом нельзя: редактор, открыв такую строку,
    /// покажет ПЕРВЫЙ заполненный вариант, и первая же правка выбросит остальные.</para>
    /// </summary>
    private static void CheckUnionArity(
        string path, DocumentType composite, IReadOnlyList<SchemaFieldInfo> inners, JsonElement value,
        IReadOnlyDictionary<Guid, DocumentType> typesById, List<ResolutionDiagnostic> diagnostics)
    {
        if (!SchemaTags.TypeHasTag(composite, typesById, FunctionalTag.TypeUnion)) return;

        // Расчётные подполя вариантами не считаются: их значение пишет ComputedFieldResolver, и к
        // моменту этой проверки оно уже проставлено. Не отфильтруй мы их — union с одним расчётным
        // подполем предъявлял бы претензию КАЖДОМУ здоровому значению, и снять её человеку нечем.
        // Редактор их тоже не показывает (#368), так что счёт обязан совпадать.
        var filled = inners
            .Where(f => !f.Computed && value.TryGetProperty(f.Key, out var v) && IsVariantFilled(v))
            .Select(f => f.Title ?? f.Key)
            .ToList();
        if (filled.Count < 2) return;

        // Про «попадёт в документ» не говорим: и тот и другой ключ уходят в data.json, а что
        // напечатается, решает блок типа в шаблоне — утверждать за него нечего.
        diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, path,
            $"«{composite.Name}» — выбор одного из вариантов, а заполнено {filled.Count}: "
            + string.Join(", ", filled.Select(t => $"«{t}»"))
            + ". В данные уйдут все; что попадёт в документ, решит блок типа в шаблоне. "
            + "Редактор откроет первый и при правке потеряет остальные.",
            "union-arity"));
    }

    /// <summary>
    /// Заполнен ли вариант. Повторяет <c>isVariantFilled</c> редактора ключ в ключ, включая неочевидное:
    /// пустой объект и пустой массив — «не выбрано» (иначе снятая ссылка, <c>{}</c>, считалась бы вторым
    /// вариантом и предупреждение сыпалось бы на здоровых данных), а <c>false</c> — ВЫБРАНО, потому что
    /// редактор судит по строковому виду значения. Разойдись мы здесь, предупреждение обещало бы потерю
    /// там, где её не будет, — или молчало бы там, где она случится.
    /// </summary>
    private static bool IsVariantFilled(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Object => v.EnumerateObject().Any(),
        JsonValueKind.Array => v.EnumerateArray().Any(),
        _ => true,
    };

    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;

    private static void Warn(List<ResolutionDiagnostic> diagnostics, string path, string message)
        => diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, path, message, "value-type"));
}
