using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataFixups;

/// <summary>
/// Разовый перенос размеров изображений из определения типа в инстансы (issue #246). Раньше
/// width/height/align/fit задавались в схеме типа (fields[].image) и подмешивались генератором по имени
/// поля; теперь размер живёт в самом значении-картинке инстанса (<c>{ src, width, ... }</c>).
/// <para>
/// Миграция: по глобальной карте (имя поля → опции из схем) обходит JSONB каждого объекта и заменяет
/// голые data-URI строки на объекты со снятыми из схемы размерами; затем вычищает блок <c>image</c>
/// из схем типов, чтобы источник размера окончательно съехал в инстанс. Идемпотентна: значения-объекты
/// не трогает, а после вычистки схем карта пустеет и обход больше не запускается.
/// </para>
/// </summary>
public static class ImageSizeToInstanceFixup
{
    public static async Task RunAsync(AppDbContext db, CancellationToken ct = default)
    {
        var types = await db.DocumentTypes.ToListAsync(ct);
        var options = SchemaImageOptions.Collect(types);
        if (options.Count == 0) return; // размеры в схемах не заданы — переносить нечего (в т.ч. свежая БД).

        // 1. Инстансы: голые data-URI под известным по имени поля ключом → объект с размером.
        var objects = await db.DomainObjects.ToListAsync(ct);
        var changed = 0;
        foreach (var obj in objects)
        {
            var migrated = MigrateDataJson(obj.Data.RootElement.GetRawText(), options);
            if (migrated is not null)
            {
                obj.SetData(JsonDocument.Parse(migrated));
                changed++;
            }
        }

        // 2. Схемы типов: убираем блок image (размер съехал в инстансы) — финализирует перенос.
        var typesStripped = 0;
        foreach (var t in types)
        {
            var stripped = StripImageFromSchema(t.Schema.RootElement.GetRawText());
            if (stripped is not null)
            {
                t.UpdateSchema(JsonDocument.Parse(stripped));
                typesStripped++;
            }
        }

        if (changed > 0 || typesStripped > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Заменяет в JSON инстанса голые data-URI на объекты-значения с размером из <paramref name="options"/>
    /// (по имени поля-ключа). Уже-объектные значения-картинки не трогает. Возвращает изменённый JSON или
    /// <c>null</c>, если менять нечего. Чистая функция — тестируется напрямую.
    /// </summary>
    public static string? MigrateDataJson(string dataJson, IReadOnlyDictionary<string, ImageRenderOptions> options)
    {
        var root = JsonNode.Parse(dataJson);
        if (root is null) return null;
        return MigrateNode(root, options) ? root.ToJsonString() : null;
    }

    /// <summary>Убирает <c>image</c> из каждого поля схемы. Возвращает очищенный JSON или <c>null</c>, если убирать нечего.</summary>
    public static string? StripImageFromSchema(string schemaJson)
    {
        var root = JsonNode.Parse(schemaJson);
        if (root is not JsonObject obj || obj["fields"] is not JsonArray fields) return null;

        var removed = false;
        foreach (var f in fields)
            if (f is JsonObject fo && fo.Remove("image")) removed = true;

        return removed ? root.ToJsonString() : null;
    }

    /// <summary>Рекурсивно заменяет голые data-URI на объекты-значения с размером; уже-объектные картинки
    /// не трогает. Возвращает true, если что-то изменил.</summary>
    private static bool MigrateNode(JsonNode? node, IReadOnlyDictionary<string, ImageRenderOptions> options)
    {
        var mutated = false;
        switch (node)
        {
            case JsonObject obj:
                // Уже мигрированное значение-картинка ({src: data-URI, ...}) — не рекурсируем внутрь.
                if (ImageValues.TryGetImageObjectSrc(obj, out _)) return false;
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue val && val.TryGetValue<string>(out var s) && ImageValues.IsDataImage(s)
                        && options.TryGetValue(key, out var opt))
                    {
                        obj[key] = ToImageObject(s, opt);
                        mutated = true;
                    }
                    else
                    {
                        mutated |= MigrateNode(child, options);
                    }
                }
                break;
            case JsonArray arr:
                // Элементы массива не имеют имени-ключа → размер не переносим (как и генератор раньше),
                // но спускаемся внутрь ради вложенных объектов.
                foreach (var item in arr) mutated |= MigrateNode(item, options);
                break;
        }
        return mutated;
    }

    private static JsonObject ToImageObject(string src, ImageRenderOptions o)
    {
        var node = new JsonObject { ["src"] = src };
        if (!string.IsNullOrEmpty(o.Width)) node["width"] = o.Width;
        if (!string.IsNullOrEmpty(o.Height)) node["height"] = o.Height;
        if (!string.IsNullOrEmpty(o.Align)) node["align"] = o.Align;
        if (!string.IsNullOrEmpty(o.Fit)) node["fit"] = o.Fit;
        return node;
    }
}
