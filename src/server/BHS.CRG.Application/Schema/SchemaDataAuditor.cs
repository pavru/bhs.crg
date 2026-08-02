using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

public enum AuditSeverity { Warning, Error }

/// <summary>Одно расхождение данных инстанса с ТЕКУЩЕЙ эффективной схемой его типа (issue #348).</summary>
public record AuditIssue(string Code, AuditSeverity Severity, string Path, string Message);

/// <summary>
/// Чистый аудитор: сравнивает СЫРЫЕ данные инстанса (domain_objects.Data) с эффективной схемой типа
/// (с наследованием) и возвращает расхождения. ОБРАТНЫЙ обход (ключи данных → флажить вне схемы) —
/// в отличие от <c>TypeStamper</c> (поля схемы → данные). MVP-категории: осиротевший ключ (ключ данных
/// отсутствует в схеме, рекурсивно во вложенных составных) и несовпадение вида значения с типом поля.
///
/// Работает над СЫРЫМИ данными (не над разрешённым контекстом): <c>$ref</c>-объекты (ссылки каталога/
/// документа) — это ссылки, вглубь них не идём; мета-ключи (<c>_</c>-префикс: _baseRef и т.п.) не данные.
///
/// Тонкий слой (базовый тип значения, шаблон и границы примитива, разбор даты) — общий со сканером
/// выпуска, <see cref="ValueTypeRules"/>, код находки <see cref="ValueType"/> (issue #642). До этого
/// аудит видел только грубое несовпадение ВИДА значения, и записи общих данных — единственные
/// объекты, которых сканер выпуска не касается вовсе, — не проверялись тонким слоем никогда.
/// </summary>
public static class SchemaDataAuditor
{
    public const string OrphanKey = "orphan-key";
    public const string TypeMismatch = "type-mismatch";
    public const string ValueType = "value-type";

    /// <param name="primitivesById">Примитивные типы для тонкого слоя. Пустой словарь — поля с
    /// <c>type=primitive</c> останутся без проверки (их правила описаны самим примитивом).</param>
    public static IReadOnlyList<AuditIssue> Audit(
        JsonElement data, Guid typeId,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById)
    {
        var issues = new List<AuditIssue>();
        if (data.ValueKind == JsonValueKind.Object)
            Walk(data, typeId, "", byId, primitivesById, issues);
        return issues;
    }

    private static void Walk(
        JsonElement obj, Guid typeId, string basePath,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<AuditIssue> issues)
    {
        var fields = DocumentTypeSchemaReader.EffectiveFields(typeId, byId).ToDictionary(f => f.Key);
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.StartsWith('_')) continue; // мета (_type/_baseRef/_typeId) — не данные схемы
            var path = basePath.Length == 0 ? p.Name : $"{basePath}.{p.Name}";
            if (!fields.TryGetValue(p.Name, out var f))
            {
                issues.Add(new(OrphanKey, AuditSeverity.Warning, path,
                    $"Ключ «{p.Name}» отсутствует в текущей схеме типа (осиротевшее поле)."));
                continue; // нет схемы для этого ключа — вглубь не идём
            }
            CheckField(f, p.Value, path, byId, primitivesById, issues);
        }
    }

    private static void CheckField(
        SchemaFieldInfo f, JsonElement value, string path,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<AuditIssue> issues)
    {
        var vk = value.ValueKind;
        if (vk == JsonValueKind.Null) return; // пусто — не расхождение

        if (DocumentTypeSchemaReader.IsMultiValued(f.Type)) // array / doc-array
        {
            if (vk != JsonValueKind.Array)
            {
                issues.Add(new(TypeMismatch, AuditSeverity.Warning, path,
                    $"Поле «{f.Key}» ожидает массив, а в данных — {KindRu(vk)}."));
                return;
            }
            if (f.TypeId is { } etid && byId.ContainsKey(etid))
            {
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && !IsRef(item))
                        Walk(item, etid, $"{path}[{i}]", byId, primitivesById, issues);
                    i++;
                }
            }
        }
        else if (DocumentTypeSchemaReader.IsSingleComposite(f.Type)) // complex / doc-ref
        {
            if (vk != JsonValueKind.Object)
                issues.Add(new(TypeMismatch, AuditSeverity.Warning, path,
                    $"Поле «{f.Key}» ожидает составной объект, а в данных — {KindRu(vk)}."));
            else if (!IsRef(value) && f.TypeId is { } tid && byId.ContainsKey(tid))
                Walk(value, tid, path, byId, primitivesById, issues); // инлайн-составной — рекурсивно
        }
        else // скаляр (string/number/date/enum/boolean/primitive/file/image)
        {
            if (vk is JsonValueKind.Object or JsonValueKind.Array && f.Type is not ("file" or "image"))
            {
                issues.Add(new(TypeMismatch, AuditSeverity.Warning, path,
                    $"Поле «{f.Key}» ожидает скалярное значение, а в данных — {KindRu(vk)}."));
                return; // вид не сошёлся — тонкий слой сказал бы то же самое второй раз
            }
            foreach (var message in ValueTypeRules.CheckScalar(f, value, primitivesById))
                issues.Add(new(ValueType, AuditSeverity.Warning, path, message));
        }
    }

    private static bool IsRef(JsonElement obj) => obj.TryGetProperty("$ref", out _);

    private static string KindRu(JsonValueKind vk) => vk switch
    {
        JsonValueKind.Object => "объект",
        JsonValueKind.Array => "массив",
        JsonValueKind.String => "строка",
        JsonValueKind.Number => "число",
        JsonValueKind.True or JsonValueKind.False => "булево",
        _ => vk.ToString(),
    };
}
