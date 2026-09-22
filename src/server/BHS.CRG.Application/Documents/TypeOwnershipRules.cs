using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Documents;

/// <summary>На что тип опирается: один родитель или одно вложенное поле.</summary>
/// <param name="TypeId">Тип, без которого этот тип не описать.</param>
/// <param name="Where">Место опоры словами: «родитель» или «поле «Наименование»».</param>
public record TypeSupport(Guid TypeId, string Where);

/// <summary>
/// Правило ТЗ CORE-30: **всё, от чего наследуется или на что опирается тип ядра, принадлежит
/// ядру.** Иначе при выключенном модуле тип ядра остался бы без родителя или без вложенного типа —
/// то есть неописуемым, хотя сам никуда не делся.
///
/// Обобщение то же самое для модулей (CORE-2, CORE-34): опираться можно на ядро и на СВОИ типы.
/// Опора модуля на чужой модуль — та же поломка, только отложенная до дня, когда чужой модуль
/// выключат.
///
/// ⚠️ **Что считается опорой, а что нет.** Опора — родитель и ВЛОЖЕНИЕ (<c>complex</c>,
/// <c>array</c>): без них форму типа не описать. Документная ссылка (<c>doc-ref</c>,
/// <c>doc-array</c>) опорой НЕ считается: это указатель на документ, то есть на данные. Пропади
/// тип по ту сторону такой ссылки — поле нечем заполнить, но сам тип описан полностью и
/// печатается. Разница не косметическая: по буквальному чтению («на что ссылается») ядру
/// достались бы документные типы исполнительной документации — через поле профиля раздела и
/// приказ подписанта, — и признак владельца перестал бы что-либо значить ровно там, ради чего
/// заводится.
/// </summary>
public static class TypeOwnershipRules
{
    /// <summary>Виды полей, которые ВКЛЮЧАЮТ чужой тип в форму этого.</summary>
    private static readonly HashSet<string> Embedding = new(StringComparer.OrdinalIgnoreCase)
        { "complex", "array" };

    /// <summary>Опоры типа: родитель и вложенные типы его собственной схемы.</summary>
    public static IReadOnlyList<TypeSupport> SupportsOf(DocumentType type)
    {
        var supports = new List<TypeSupport>();
        if (type.ParentId is { } parent) supports.Add(new TypeSupport(parent, "родитель"));
        CollectEmbedded(type.Schema.RootElement, supports);
        return supports;
    }

    /// <summary>
    /// Чем нарушено правило — словами, готовыми к показу человеку. Пусто — нарушений нет.
    /// </summary>
    /// <param name="byId">Все типы; опора, которой нет в словаре, пропускается — её отсутствие
    /// ловится другими проверками, и выдавать за нарушение прав чужую поломку незачем.</param>
    /// <param name="onlySupport">Проверять только опору на этот тип. Нужно, чтобы правка одного
    /// типа не спотыкалась о ЧУЖОЕ расхождение, уже лежащее в базе (например, приехавшее чужой
    /// копией): иначе один застарелый разлад запирает правки не связанных с ним типов, и чинить
    /// его остаётся правкой базы руками. Найдено ревью PR #1002.</param>
    public static IReadOnlyList<string> Violations(
        DocumentType type, IReadOnlyDictionary<Guid, DocumentType> byId, Guid? onlySupport = null)
    {
        var problems = new List<string>();
        foreach (var support in SupportsOf(type))
        {
            if (onlySupport is { } only && support.TypeId != only) continue;
            if (!byId.TryGetValue(support.TypeId, out var target)) continue;
            if (Allows(type.Module, target.Module)) continue;
            problems.Add(
                $"тип «{type.Name}» принадлежит {OwnerWords(type.Module)}, а его {support.Where} — " +
                $"тип «{target.Name}» — принадлежит {OwnerWords(target.Module)}");
        }
        return problems;
    }

    /// <summary>Опора разрешена на ядро и на своего владельца.</summary>
    public static bool Allows(string owner, string supportOwner)
        => TypeOwner.IsCore(supportOwner)
           || string.Equals(owner, supportOwner, StringComparison.OrdinalIgnoreCase);

    public static string OwnerWords(string module) => TypeOwner.IsCore(module) ? "ядру" : $"модулю «{module}»";

    /// <summary>
    /// Собирает <c>typeId</c> вложенных полей. Обход идёт по всему дереву схемы, а не только по
    /// верхнему <c>fields</c>: поля лежат ещё и в группах и в переопределениях, и список,
    /// пропустивший одно место, выглядел бы исчерпывающим, не будучи им.
    /// </summary>
    private static void CollectEmbedded(JsonElement node, List<TypeSupport> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) CollectEmbedded(item, into);
                break;
            case JsonValueKind.Object:
                if (node.TryGetProperty("typeId", out var idNode)
                    && idNode.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idNode.GetString(), out var id)
                    && node.TryGetProperty("type", out var kindNode)
                    && kindNode.ValueKind == JsonValueKind.String
                    && Embedding.Contains(kindNode.GetString() ?? ""))
                {
                    var key = node.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString() : null;
                    into.Add(new TypeSupport(id, key is null ? "вложенное поле" : $"поле «{key}»"));
                }
                foreach (var property in node.EnumerateObject()) CollectEmbedded(property.Value, into);
                break;
        }
    }
}
