using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>Шаг пути от записи вглубь её данных: ключ поля схемы; массив — каждая его строка.</summary>
public sealed record PathStep(string Key, bool IsArray);

/// <summary>
/// Путь от записи-владельца до поля подсказки: шаги по составным полям и массивам, затем ключ поля
/// (<c>Работы[*].Наименование</c>, <c>Адрес.Регион</c>).
/// </summary>
public sealed record SuggestionPath(IReadOnlyList<PathStep> Steps, string Field)
{
    public override string ToString() =>
        string.Concat(Steps.Select(s => s.Key + (s.IsArray ? "[*]." : "."))) + Field;
}

/// <summary>Записи типа-владельца, внутри которых по этим путям лежит поле (issue #1532).</summary>
public sealed record NestedSource(Guid OwnerTypeId, IReadOnlyList<SuggestionPath> Paths);

/// <summary>
/// Пути к полю внутри чужих записей — ЧИСТАЯ функция над схемами (issue #1532, эпик #1527).
///
/// <para><b>Путь строится по ключам схемы, а не поиском ключа в данных.</b> Рекурсивный <c>$.**</c>
/// смешал бы «Организация.Наименование» с «Работа.Наименование»: у обоих тот же ключ, а поле — разное.
/// Здесь в путь попадает только составное поле или массив, чей <c>typeId</c> — тип семьи поля.</para>
///
/// <para><b>Ссылки путём не порождаются</b>: <c>doc-ref</c> и <c>doc-array</c> держат ссылку на чужую
/// запись, а её значение уже учтено верхним уровнем её типа. Ссылку на запись справочника внутри
/// составного поля (<c>{ $ref }</c>) отсекает чтение — по схеме её от встроенного значения не отличить.</para>
///
/// <para><b>Наследование — по эффективной схеме</b> каждого типа: потомок владельца, унаследовавший
/// массив, — тоже владелец, и путь у него тот же; перекрывший ключ массива своим — уже нет.</para>
///
/// <para><b>Пределы</b> — глубина <see cref="MaxDepth" /> и число путей <see cref="MaxPaths" />.</para>
/// </summary>
public static class SuggestionPaths
{
    /// <summary>Сколько уровней вложенности (составных полей и массивов) читается — не больше.</summary>
    public const int MaxDepth = 6;

    /// <summary>
    /// Сколько путей читается за запрос — не больше, по всем владельцам вместе. Каждый путь — свой разбор
    /// данных у каждой записи окна, а у типа, вложенного в себя несколькими полями, путей с глубиной
    /// становится экспоненциально больше (три таких поля — больше тысячи путей к глубине 6), и поле
    /// отвечало бы 503 на каждую букву. Пути набираются от мелких к глубоким: срезаются глубокие.
    /// </summary>
    public const int MaxPaths = 32;

    /// <summary>
    /// Типы-владельцы и пути к <paramref name="field" /> у них — для поля, которое видно у типов
    /// <paramref name="members" />. Тип в ответе — любого хранения и вида: отбор по ним делает план.
    /// </summary>
    public static IReadOnlyDictionary<Guid, List<SuggestionPath>> For(
        IReadOnlyCollection<Guid> members, string field, IReadOnlyDictionary<Guid, DocumentType> types)
    {
        var embedding = Embedding(types);

        // От записи типа-ключа до поля: шаги. Начало — сами члены семьи, без шагов.
        var frontier = members.Distinct().ToDictionary(id => id, _ => new List<PathStep[]> { Array.Empty<PathStep>() });
        var result = new Dictionary<Guid, List<SuggestionPath>>();
        var room = MaxPaths;
        for (var depth = 1; depth <= MaxDepth && frontier.Count > 0 && room > 0; depth++)
        {
            var next = new Dictionary<Guid, List<PathStep[]>>();
            foreach (var (owner, fields) in embedding)
                foreach (var f in fields)
                {
                    if (room == 0 || !frontier.TryGetValue(f.TypeId!.Value, out var suffixes)) continue;
                    var step = new PathStep(f.Key, f.Type == "array");
                    var taken = suffixes.Take(room).Select(s => s.Prepend(step).ToArray()).ToList();
                    room -= taken.Count;
                    if (!next.TryGetValue(owner, out var list)) next[owner] = list = [];
                    list.AddRange(taken);
                }

            foreach (var (owner, list) in next)
            {
                if (!result.TryGetValue(owner, out var paths)) result[owner] = paths = [];
                paths.AddRange(list.Select(steps => new SuggestionPath(steps, field)));
            }
            frontier = next;
        }
        return result;
    }

    /// <summary>
    /// Поля, ведущие внутрь (составное и массив с типом строки; ссылки doc-ref и doc-array — нет), по
    /// эффективной схеме типа. Адрес зовут на каждую букву, поэтому эффективная схема собирается только у
    /// типов, в цепочке которых такое поле объявлено: остальным строить её незачем — вложений у них нет.
    /// </summary>
    private static Dictionary<Guid, List<SchemaFieldInfo>> Embedding(IReadOnlyDictionary<Guid, DocumentType> types)
    {
        static bool Leads(SchemaFieldInfo f) => f.TypeId is not null && f.Type is "complex" or "array";

        var declaring = types.Values
            .Where(t => DocumentTypeSchemaReader.OwnFields(t.Schema).Any(Leads))
            .Select(t => t.Id)
            .ToHashSet();
        return types.Keys
            .Where(id => InChain(id, declaring, types))
            .ToDictionary(id => id, id => DocumentTypeSchemaReader.EffectiveFields(id, types).Where(Leads).ToList());
    }

    /// <summary>Есть ли в цепочке типа (он сам и предки) один из <paramref name="ids" />.</summary>
    private static bool InChain(Guid id, HashSet<Guid> ids, IReadOnlyDictionary<Guid, DocumentType> types)
    {
        var current = types.GetValueOrDefault(id);
        for (var guard = 0; current is not null && guard < 32; guard++)
        {
            if (ids.Contains(current.Id)) return true;
            current = current.ParentId is { } p ? types.GetValueOrDefault(p) : null;
        }
        return false;
    }
}
