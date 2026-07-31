using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Пространство имён НЕ «…Infrastructure.Migration»: оно перекрывало бы тип Migration из EF Core во
// всех файлах папки Migrations/ — сборка падала с «Migration is a namespace but is used like a type».
namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Что сделала (или сделала бы) миграция картинок.</summary>
/// <param name="Objects">Записей затронуто.</param>
/// <param name="Images">Картинок перенесено.</param>
/// <param name="Bytes">Освобождено из JSONB (размер самих data-URI).</param>
public record ImageMigrationReport(int Objects, int Images, long Bytes);

/// <summary>
/// Разовый перенос картинок из JSONB в блоб-хранилище (issue #522).
///
/// НЕ EF-миграция сознательно: миграции применяются на старте приложения, а блоб-хранилище на этот
/// момент может быть недоступно — получили бы падающий или наполовину сконвертированный старт.
/// Это действие администратора: он выбирает момент и видит отчёт.
///
/// Идемпотентна и перезапускаема: узлы, уже переехавшие, пропускаются. Перезапускаемость не
/// теоретическая — восстановление старого бэкапа заново впрыскивает data-URI, и миграцию можно будет
/// прогнать снова.
/// </summary>
public class ImageBlobMigration(AppDbContext db, IBlobStorage blob)
{
    /// <param name="dryRun">Только посчитать: ничего не грузить и не сохранять.</param>
    public async Task<ImageMigrationReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var objects = 0;
        var images = 0;
        var bytes = 0L;

        // Фильтруем в памяти: записей каталога десятки, а выразить «в JSONB есть data:image» через
        // EF пришлось бы сырым SQL — цена не окупается.
        var all = await db.DomainObjects.ToListAsync(ct);
        var candidates = all.Where(o => o.Data.RootElement.GetRawText().Contains("data:image", StringComparison.Ordinal));

        foreach (var obj in candidates)
        {
            var node = JsonNode.Parse(obj.Data.RootElement.GetRawText());
            if (node is null) continue;

            var moved = await MoveAsync(node, dryRun, ct);
            if (moved.Count == 0) continue;

            objects++;
            images += moved.Count;
            bytes += moved.Bytes;

            if (!dryRun)
            {
                obj.SetData(JsonDocument.Parse(node.ToJsonString()));
                db.DomainObjects.Update(obj);
            }
        }

        if (!dryRun && objects > 0) await db.SaveChangesAsync(ct);
        return new ImageMigrationReport(objects, images, bytes);
    }

    private async Task<(int Count, long Bytes)> MoveAsync(JsonNode node, bool dryRun, CancellationToken ct)
    {
        var count = 0;
        var bytes = 0L;

        // Возвращаем ПАРУ «узнали ли картинку» и «чем заменить»: без первого флага сухой прогон
        // считал каждую картинку дважды — замены нет, обход спускается внутрь узла {src, ...} и
        // видит ту же data-URI второй раз. Число из отчёта идёт человеку, ему врать нельзя.
        async Task<(bool Handled, JsonNode? Replacement)> Convert(JsonNode? child)
        {
            // Голая строка (легаси) и объект {src, ...} — обе формы переезжают в одну и ту же ссылку.
            var (dataUri, options) = child switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) && ImageValues.IsDataImage(s) => (s, null as JsonObject),
                JsonObject o when ImageValues.TryGetImageObjectSrc(o, out var src) => (src, o),
                _ => (null, null),
            };
            if (dataUri is null) return (false, null);

            count++;
            bytes += dataUri.Length;
            if (dryRun) return (true, null);

            var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
            var mime = dataUri[5..dataUri.IndexOf(';', StringComparison.Ordinal)];
            var raw = System.Convert.FromBase64String(dataUri[(comma + 1)..]);
            var ext = mime switch
            {
                "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif",
                "image/webp" => "webp", "image/svg+xml" => "svg", _ => "bin",
            };
            var path = await blob.UploadAsync($"image.{ext}", new MemoryStream(raw), mime, ct);

            var replacement = new JsonObject
            {
                ["$type"] = ImageValues.BlobTypeMarker,
                ["blobPath"] = path,
                ["fileName"] = $"image.{ext}",
                ["mimeType"] = mime,
            };
            // Опции размера переносим как есть — иначе печать «поедет» в вёрстке документа.
            foreach (var key in ImageValues.OptionKeys)
                if (options?[key] is JsonValue opt) replacement[key] = opt.DeepClone();
            return (true, replacement);
        }

        async Task WalkAsync(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(kv => kv.Key).ToList())
                    {
                        var (handled, replacement) = await Convert(obj[key]);
                        if (replacement is not null) obj[key] = replacement;
                        else if (!handled) await WalkAsync(obj[key]);
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var (handled, replacement) = await Convert(arr[i]);
                        if (replacement is not null) arr[i] = replacement;
                        else if (!handled) await WalkAsync(arr[i]);
                    }
                    break;
            }
        }

        await WalkAsync(node);
        return (count, bytes);
    }
}
