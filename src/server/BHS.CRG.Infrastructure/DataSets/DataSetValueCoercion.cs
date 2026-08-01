using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Common;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Приведение значения из ячейки набора к объявленному типу поля (issue #466).
///
/// До этого привязка клала текст ячейки как есть, и числовые поля накапливали строки: на рабочем
/// комплекте 246 предупреждений из 247 были именно этим — количества материалов и длины кабеля.
///
/// Разбор общий со сверкой (<see cref="QuantityParser"/>): одна и та же ячейка обязана читаться
/// одинаково и когда значение кладётся в документ, и когда оно сравнивается. Разойдись эти два
/// разбора — документ и отчёт показали бы разные числа по одним данным.
///
/// Применяется при РЕЗОЛВЕ, но не в предпросмотре: там правильно показывать то, что в источнике.
/// Расхождение осознанное — такое же, как на ветке <c>@@ref</c>, где резолв даёт ссылку, а превью
/// маркер.
/// </summary>
public static class DataSetValueCoercion
{
    /// <summary>Глубина обхода собранных объектов — защита от патологически вложенных схем.</summary>
    private const int MaxDepth = 6;

    /// <summary>
    /// Значение поля в объявленном типе. Приводим только числа: даты хранятся строкой ISO (объявленный
    /// тип <c>date</c> строку и ожидает), логические из таблиц практически не приходят — выдумывать
    /// правила под несуществующие случаи не нужно.
    /// </summary>
    public static object? Coerce(
        object? value, SchemaFieldInfo? field,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        IReadOnlyDictionary<Guid, DocumentType>? typesById = null, int depth = 0)
    {
        if (field is null || depth >= MaxDepth) return value;

        // Собранный @@inline-объект: числа в нём лежат ГЛУБЖЕ, чем поле строки. На рабочих данных
        // длины кабеля живут в «КабельнаяЛинияТрасса.КабельПроложено.ДлинаЛинии» — без обхода внутрь
        // они так и остались бы строками.
        if (value is Dictionary<string, object?> nested)
            return CoerceObject(nested, field.TypeId, primitivesById, typesById, depth);

        // Приводим только сырой текст ячейки: ссылки (@@ref) и файлы уже имеют свою форму.
        if (value is not string text) return value;
        if (!IsNumeric(field, primitivesById)) return value;   // в строковом поле пустая строка законна

        // Пустая ячейка — ОТСУТСТВИЕ значения, а не значение неверного типа (issue #544). Раньше она
        // возвращалась как есть и оседала пустой строкой в поле, объявленном числом: на протоколе
        // измерений изоляции из 473 числовых ячеек 238 были такими, и проверка выдавала 238
        // предупреждений «ожидается число, а хранится строка» — за ними уже ничего не разглядеть.
        //
        // Возвращаем null: вызывающие пишут значение под `if (value is not null)`, поэтому ключа
        // просто не будет. Именно отсутствие ключа, а не null: домашняя идиома шаблонов —
        // `it.at("X", default: "")` и `dig(it, "X", default: …)` — отсутствующий ключ обрабатывает
        // верно, а на null сломалась бы (`it.at("Телефон", default: "") != ""` пройдёт проверку и
        // упадёт на `"тел.: " + none`).
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Не разобралось — оставляем строку. Молча подменить на ноль нельзя: ноль это заявленное
        // количество, а неразобранное — неизвестность; расхождение останется видимым в проверке.
        return QuantityParser.TryParse(text, out var number) ? number : value;
    }

    /// <summary>
    /// Обход собранного объекта по схеме его типа. Ключи, для которых приведение дало null (пустая
    /// числовая ячейка, issue #544), удаляются; объект, оставшийся БЕЗ значений, возвращается как
    /// null — «нет значений» на всех уровнях выглядит одинаково, и это то же правило, по которому
    /// собирает объекты <c>DataSetMappingApplier</c> (<c>obj.Count == 0 ? null : obj</c>).
    /// </summary>
    public static Dictionary<string, object?>? CoerceObject(
        Dictionary<string, object?> obj, Guid? typeId,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        IReadOnlyDictionary<Guid, DocumentType>? typesById, int depth = 0)
    {
        if (typeId is not { } id || typesById is null || !typesById.ContainsKey(id)) return obj;

        var fields = DocumentTypeSchemaReader.EffectiveFields(id, typesById)
            .GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var key in obj.Keys.ToList())
        {
            if (!fields.TryGetValue(key, out var f)) continue;
            var coerced = Coerce(obj[key], f, primitivesById, typesById, depth + 1);
            // Ключ с null УДАЛЯЕМ, а не оставляем (issue #544): шаблоны читают вложенные поля через
            // at/dig с умолчанием, и ключ со значением none проходит их проверки, а потом ломает
            // конкатенацию. Правило шире, чем пустая числовая ячейка: любой null здесь означает
            // «значения нет», и вложенный объект, у которого не осталось значений, тоже null.
            if (coerced is null) obj.Remove(key);
            else obj[key] = coerced;
        }

        return obj.Count == 0 ? null : obj;
    }

    private static bool IsNumeric(
        SchemaFieldInfo field, IReadOnlyDictionary<Guid, PrimitiveType> primitivesById) => field.Type switch
    {
        "number" => true,
        "primitive" => field.TypeId is { } id
            && primitivesById.TryGetValue(id, out var p)
            && p.BaseType == "number",
        _ => false,
    };
}
