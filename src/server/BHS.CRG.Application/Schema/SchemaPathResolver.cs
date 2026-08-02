using System.Text.RegularExpressions;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Поле схемы по пути аудита (issue #643): <c>Ключ</c>, <c>Ключ.Внутренний</c>, <c>Работы[0].Порядок</c>.
///
/// Обратная сторона <see cref="JsonPathEditor"/>: тот идёт по ДАННЫМ, этот — по СХЕМЕ. Нужен там, где
/// исправление зависит от объявленного типа: чтобы привести значение, надо знать, к чему приводить, а
/// в находке аудита есть только путь.
///
/// Индекс элемента массива схему не меняет — у всех строк таблицы один тип, — поэтому <c>[3]</c>
/// просто пропускается.
/// </summary>
public static class SchemaPathResolver
{
    private static readonly Regex Segment = new(@"([^.\[\]]+)|\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>Глубина обхода — та же защита от патологически вложенных схем, что и у сканеров.</summary>
    private const int MaxDepth = 6;

    public static SchemaFieldInfo? FieldAt(string path, Guid rootTypeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var typeId = rootTypeId;
        SchemaFieldInfo? field = null;
        var depth = 0;

        foreach (Match m in Segment.Matches(path))
        {
            if (m.Groups[2].Success) continue; // индекс строки таблицы: тип строк один на всю таблицу
            if (++depth > MaxDepth) return null;

            if (!byId.ContainsKey(typeId)) return null;
            field = DocumentTypeSchemaReader.EffectiveFields(typeId, byId).FirstOrDefault(f => f.Key == m.Groups[1].Value);
            if (field is null) return null;
            // Следующий сегмент читается по типу текущего поля; у скаляра его нет — значит путь
            // ведёт внутрь того, что внутренностей не имеет.
            if (field.TypeId is { } next) typeId = next;
        }

        return field;
    }
}
