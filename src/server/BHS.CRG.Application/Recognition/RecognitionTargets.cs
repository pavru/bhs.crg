using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Recognition;

/// <summary>Что владелец профиля делает с объявленной целью — зеркало контракта модулей (ТЗ CORE-2).</summary>
public enum RecognitionTargetUse { Field, Input, Unavailable }

/// <summary>Цель поля профиля, объявленная владельцем (issue #1483).</summary>
public sealed record RecognitionTargetDeclaration(
    string Key, RecognitionTargetUse Use, string? Why, IReadOnlyList<string> LegacyKeys);

/// <summary>Куда владелец раскладывает шапку профиля: код своего типа записи и объявленные цели.</summary>
public sealed record RecognitionTargets(string RecordType, IReadOnlyList<RecognitionTargetDeclaration> Fields);

/// <summary>Откуда цель: объявлена владельцем, дописана в тип заказчиком, служебный вход.</summary>
public enum RecognitionTargetOrigin { Declared, Type, Input }

/// <summary>Решённая цель поля профиля.</summary>
/// <param name="Key">Ключ поля записи (у прежнего ключа профиля — нынешний).</param>
/// <param name="Type">Вид значения: у поля с типом из реестра — его базовый вид.</param>
/// <param name="Unavailable">Поле есть, но распознаванием не заполняется — почему.</param>
/// <param name="Title">Подпись поля из схемы; поля в схеме нет — ключ.</param>
public sealed record ResolvedRecognitionTarget(
    string Key, RecognitionTargetOrigin Origin, string Type, string? Unavailable = null, string? Title = null);

/// <summary>
/// Сопоставление «поле профиля → поле записи» (issue #1483, разбор Архитектора 18.10.2026, §1, §4).
///
/// <para><b>Правило одно:</b> ключ поля профиля равен ключу поля записи — значение ложится в это поле.
/// Цели объявляет владелец профиля (модуль), поля, дописанные заказчиком в тип, добавляются сами: их
/// знает только ядро. Объявленное старше схемы — модуль разбирает свои поля своим разбором.</para>
///
/// <para><b>Совпадение точное</b> (Ordinal): без регистра, без «ё/е», без обрезки — так же, как ядро
/// проверяет уникальность ключей. Угадывание сделало бы опечатку в ключе тихой, а её видно в итоге
/// распознавания.</para>
///
/// <para>⚠️ Каждый вид поля ядра здесь РЕШЁН (<see cref="Kinds" />): заполняется или нет и почему.
/// Новый вид без решения краснеет в тесте, сверяющем перечень с видами полей редактора схемы, — а не
/// проходит молча ни в «заполняется», ни в «нет».</para>
/// </summary>
public static class RecognitionTargetMatcher
{
    /// <summary>
    /// Виды полей схемы и решение по каждому: <c>null</c> — заполняется по своему виду; иначе причина.
    /// <c>primitive</c> решается по базовому виду типа из реестра (<see cref="Classify" />).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> Kinds = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["string"] = null,
        ["text"] = null,
        ["number"] = null,
        ["date"] = null,
        ["primitive"] = null,
        ["boolean"] = "флажок: на бумаге у него нет значения, которое можно прочитать",
        ["enum"] = "выбор из списка: вариант выбирает человек",
        ["complex"] = "ссылка на запись: её выбирает человек",
        ["doc-ref"] = "ссылка на документ: её выбирает человек",
        ["array"] = "список: распознавание шапки читает одно значение",
        ["doc-array"] = "список документов: распознавание шапки читает одно значение",
        ["file"] = "файл",
        ["image"] = "картинка",
    };

    /// <param name="keys">Ключи полей профиля, которые были в запросе.</param>
    /// <param name="declared">Объявление владельца; <c>null</c> — профиль раскладывается только в наборы данных.</param>
    /// <param name="fields">Эффективные поля типа записи; пусто — тип не заведён (тогда остаются только объявленные).</param>
    /// <param name="primitives">Типы полей из реестра — базовый вид поля <c>primitive</c>.</param>
    public static IReadOnlyDictionary<string, ResolvedRecognitionTarget?> Resolve(
        IEnumerable<string> keys, RecognitionTargets? declared, IReadOnlyList<SchemaFieldInfo> fields,
        IReadOnlyDictionary<Guid, PrimitiveType> primitives)
    {
        var result = new Dictionary<string, ResolvedRecognitionTarget?>(StringComparer.Ordinal);
        if (declared is null)
        {
            foreach (var key in keys) result[key] = null;
            return result;
        }

        var byKey = declared.Fields.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var byLegacy = declared.Fields
            .SelectMany(t => t.LegacyKeys.Select(legacy => (legacy, t)))
            .ToDictionary(p => p.legacy, p => p.t, StringComparer.Ordinal);
        var schema = new Dictionary<string, SchemaFieldInfo>(StringComparer.Ordinal);
        foreach (var field in fields) schema.TryAdd(field.Key, field);

        foreach (var key in keys)
        {
            if ((byKey.GetValueOrDefault(key) ?? byLegacy.GetValueOrDefault(key)) is { } target)
            {
                var field = schema.GetValueOrDefault(target.Key);
                var (type, title) = (field?.Type ?? "string", field?.Title ?? target.Key);
                result[key] = target.Use switch
                {
                    RecognitionTargetUse.Input => new(target.Key, RecognitionTargetOrigin.Input, "string", Title: title),
                    RecognitionTargetUse.Unavailable => new(target.Key, RecognitionTargetOrigin.Declared, type,
                        target.Why ?? "поле не заполняется распознаванием", title),
                    _ => new(target.Key, RecognitionTargetOrigin.Declared, type, Title: title),
                };
            }
            else
                result[key] = schema.TryGetValue(key, out var field) ? Classify(field, primitives) : null;
        }
        return result;
    }

    /// <summary>Поле, дописанное в тип заказчиком: заполняется ли и каким видом значения.</summary>
    public static ResolvedRecognitionTarget Classify(SchemaFieldInfo field, IReadOnlyDictionary<Guid, PrimitiveType> primitives)
    {
        var title = field.Title ?? field.Key;
        ResolvedRecognitionTarget Off(string why) => new(field.Key, RecognitionTargetOrigin.Type, field.Type, why, title);

        if (field.Computed) return Off("расчётное поле: значение считает выражение типа");
        if (field.Locked) return Off("значение кладёт код модуля");
        if (!Kinds.TryGetValue(field.Type, out var why)) return Off($"вид поля «{field.Type}» распознаванию не знаком");
        if (why is not null) return Off(why);
        if (field.Type != "primitive") return new(field.Key, RecognitionTargetOrigin.Type, field.Type, Title: title);

        // Тип из реестра — по базовому виду: ограничения (шаблон, длину, границы) проверит охрана записи.
        if (field.TypeId is not { } id || !primitives.TryGetValue(id, out var primitive))
            return Off("тип поля не найден в реестре типов");
        return primitive.BaseType is "string" or "number" or "date"
            ? new(field.Key, RecognitionTargetOrigin.Type, primitive.BaseType, Title: title)
            : Off($"базовый вид типа «{primitive.Name}» распознаванием не заполняется");
    }
}
