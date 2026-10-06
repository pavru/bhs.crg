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
/// <param name="Failed">Картинок не удалось обработать (битый base64, недоступное хранилище).</param>
/// <param name="Downscaled">Картинок уменьшено (оригинал при этом сохранён).</param>
/// <param name="SavedBytes">Сколько весили уменьшенные картинки и сколько весят копии — разница.</param>
public record ImageMigrationReport(
    int Objects, int Images, long Bytes, int Failed = 0, int Downscaled = 0, long SavedBytes = 0);

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
    /// <summary>Сколько раз запись перечитывается, если её меняют прямо во время переноса.</summary>
    private const int Attempts = 3;

    /// <summary>
    /// «Прочитал — преобразовал — записал, ЕСЛИ строку тем временем не меняли» (issue #1232).
    ///
    /// <para>Между чтением и записью здесь лежит выгрузка картинок в хранилище, то есть секунды:
    /// правка формы, пришедшая в них, стиралась снимком, снятым до неё. Блокировка строки на время
    /// выгрузки не годится — форма ждала бы хранилище, а зависшее хранилище держало бы и строку, и
    /// открытую транзакцию. Поэтому запись условная, одним UPDATE: изменили строку — он не тронет
    /// ничего, и запись проходится заново, по свежим данным. Картинки первой попытки остаются в
    /// хранилище без владельца — их заберёт уборка сирот.</para>
    ///
    /// <para>Не удалось за <see cref="Attempts" /> попытки — запись остаётся как была и попадает в
    /// число неудач отчёта: следующий прогон её заберёт.</para>
    /// </summary>
    private static async Task<((int Count, long Amount, int Failed)? Result, bool GaveUp)> RewriteAsync<TSeen>(
        bool dryRun,
        Func<Task<(string Json, TSeen Seen)?>> read,
        Func<JsonNode, Task<(int Count, long Amount, int Failed)>> transform,
        Func<TSeen, string, Task<bool>> saveIfUnchanged)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await read() is not { } row) return (null, false);   // запись успели удалить
            if (JsonNode.Parse(row.Json) is not { } node) return (null, false);

            var result = await transform(node);
            if (result.Count == 0 || dryRun) return (result, false);
            if (await saveIfUnchanged(row.Seen, node.ToJsonString())) return (result, false);
        }
        return (null, true);
    }

    private async Task<(string Json, uint Seen)?> ReadObjectAsync(Guid id, CancellationToken ct) =>
        await db.DomainObjects.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new { o.Data, o.RowVersion }).FirstOrDefaultAsync(ct) is { } row
            ? (row.Data.RootElement.GetRawText(), row.RowVersion)
            : null;

    /// <summary>Версия строки — та же, что сверяет форма (issue #1214): её двигает любая запись.</summary>
    private async Task<bool> SaveObjectAsync(Guid id, uint seen, string json, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE domain_objects SET "Data" = {json}::jsonb, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {id} AND xmin::text::bigint = {(long)seen}
            """, ct) > 0;

    private async Task<(string Json, string Seen)?> ReadQualityAsync(Guid id, CancellationToken ct) =>
        await db.QualityDocuments.AsNoTracking().Where(d => d.Id == id)
            .Select(d => d.Requisites).FirstOrDefaultAsync(ct) is { } requisites
            ? (requisites.RootElement.GetRawText(), requisites.RootElement.GetRawText())
            : null;

    /// <summary>У документа качества версии строки в модели нет — сверяются сами реквизиты.</summary>
    private async Task<bool> SaveQualityAsync(Guid id, string seen, string json, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE quality_documents SET "Requisites" = {json}::jsonb, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {id} AND "Requisites" = {seen}::jsonb
            """, ct) > 0;

    /// <param name="dryRun">Только посчитать: ничего не грузить и не сохранять.</param>
    public async Task<ImageMigrationReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var objects = 0;
        var images = 0;
        var bytes = 0L;

        // Отбор ИДЁТ В БАЗЕ (issue #532). Читать всё в память нельзя: в этой же таблице лежат
        // экземпляры документов, то есть ровно те многомегабайтные JSONB, ради которых миграция и
        // затевается, — мы бы вытащили их целиком, да ещё и пересобрали в строку на каждую запись.
        var sql = "SELECT \"Id\" FROM domain_objects WHERE \"Data\"::text LIKE '%data:image%'";
        var ids = await db.Database.SqlQueryRaw<Guid>(sql).ToListAsync(ct);

        var failed = 0;
        // Сохраняем ПОЗАПИСНО (issue #532): один общий SaveChanges держал бы весь перенос одной
        // транзакцией, а правки, сделанные людьми во время прогона, затирались бы снимком, снятым
        // до его начала.
        foreach (var id in ids)
        {
            var (moved, gaveUp) = await RewriteAsync(dryRun,
                () => ReadObjectAsync(id, ct), node => MoveAsync(node, dryRun, ct),
                (seen, json) => SaveObjectAsync(id, seen, json, ct));
            if (gaveUp) failed++;
            if (moved is not { } m) continue;
            failed += m.Failed;
            if (m.Count == 0) continue;

            objects++;
            images += m.Count;
            bytes += m.Amount;
        }

        // Документы качества хранят реквизиты в своей таблице, и поле-картинка там тоже бывает
        // (QualityDocForm рисует ImageField). Без этого прохода отчёт «переносить больше нечего»
        // означал бы «в domain_objects нечего», а человек прочитал бы его как «JSONB чист» (#532).
        var qualitySql = "SELECT \"Id\" FROM quality_documents WHERE \"Requisites\"::text LIKE '%data:image%'";
        foreach (var id in await db.Database.SqlQueryRaw<Guid>(qualitySql).ToListAsync(ct))
        {
            var (moved, gaveUp) = await RewriteAsync(dryRun,
                () => ReadQualityAsync(id, ct), node => MoveAsync(node, dryRun, ct),
                (seen, json) => SaveQualityAsync(id, seen, json, ct));
            if (gaveUp) failed++;
            if (moved is not { } m) continue;
            failed += m.Failed;
            if (m.Count == 0) continue;

            objects++;
            images += m.Count;
            bytes += m.Amount;
        }

        // Второй проход — УМЕНЬШЕНИЕ уже переехавших картинок (issue #523). Отдельно от переноса,
        // потому что это разные вопросы: перенос убирает двоичное из JSONB, уменьшение сокращает сам
        // блоб. Оригинал сохраняется — уменьшение остаётся производной и здесь.
        var downscaled = 0;
        var saved = 0L;
        var blobSql = "SELECT \"Id\" FROM domain_objects WHERE \"Data\"::text LIKE '%\"$type\": \"image\"%' OR \"Data\"::text LIKE '%\"$type\":\"image\"%'";
        foreach (var id in await db.Database.SqlQueryRaw<Guid>(blobSql).ToListAsync(ct))
        {
            var (shrunk, gaveUp) = await RewriteAsync(dryRun,
                () => ReadObjectAsync(id, ct), node => ShrinkAsync(node, dryRun, ct),
                (seen, json) => SaveObjectAsync(id, seen, json, ct));
            if (gaveUp) failed++;
            if (shrunk is not { } s) continue;
            failed += s.Failed;
            if (s.Count == 0) continue;

            downscaled += s.Count;
            saved += s.Amount;
        }

        return new ImageMigrationReport(objects, images, bytes, failed, downscaled, saved);
    }

    private async Task<(int Count, long Bytes, int Failed)> MoveAsync(JsonNode node, bool dryRun, CancellationToken ct)
    {
        var count = 0;
        var bytes = 0L;
        var failed = 0;

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

            if (dryRun) { count++; bytes += dataUri.Length; return (true, null); }

            // Отказ ОДНОЙ картинки не должен губить весь прогон (issue #532): битый base64 в системе
            // ожидаем — материализатор Typst его молча пропускает, — а перебой в хранилище на девятой
            // записи из десяти оставил бы восемь перенесённых и ни одной сохранённой.
            try
            {
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

                count++;
                bytes += dataUri.Length;
                return (true, replacement);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                failed++;
                return (true, null);   // узел оставляем как был — заберём следующим прогоном
            }
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
        return (count, bytes, failed);
    }

    /// <summary>
    /// Уменьшает уже переехавшие картинки, сохраняя оригинал (issue #523).
    ///
    /// Узел, у которого уже есть <c>originalBlobPath</c>, пропускаем — он уже уменьшен, и повторный
    /// прогон обязан быть безвредным. Сухой прогон СЧИТАЕТ ЧЕСТНО: скачивает и уменьшает, но ничего
    /// не сохраняет. Иначе экран подтверждения показывал бы выдуманные числа, а решать по ним
    /// человеку.
    /// </summary>
    private async Task<(int Count, long Saved, int Failed)> ShrinkAsync(
        JsonNode node, bool dryRun, CancellationToken ct)
    {
        var count = 0;
        var saved = 0L;
        var failed = 0;

        async Task WalkAsync(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    if (obj["$type"] is JsonValue t && t.TryGetValue<string>(out var marker) && marker == "image")
                    {
                        if (obj["originalBlobPath"] is not null) return;   // уже уменьшена
                        if (obj["blobPath"] is not JsonValue bv || !bv.TryGetValue<string>(out var path)) return;

                        try
                        {
                            byte[] source;
                            await using (var stream = await blob.DownloadAsync(path, ct))
                            {
                                using var ms = new MemoryStream();
                                await stream.CopyToAsync(ms, ct);
                                source = ms.ToArray();
                            }

                            var mime = obj["mimeType"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : "image/png";
                            var down = ImageDownscaler.Downscale(source, mime);
                            // Копию берём только если легче — то же правило, что и при загрузке.
                            if (down.Bytes is null || down.Bytes.Length >= source.Length) return;

                            count++;
                            saved += source.Length - down.Bytes.Length;
                            if (dryRun) return;

                            var ext = down.MimeType == "image/png" ? "png" : "jpg";
                            var newPath = await blob.UploadAsync($"image_{down.Width}.{ext}",
                                new MemoryStream(down.Bytes), down.MimeType, ct);

                            obj["originalBlobPath"] = path;   // оригинал остаётся под рукой
                            obj["blobPath"] = newPath;
                            obj["mimeType"] = down.MimeType;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { failed++; }
                        return;
                    }
                    foreach (var key in obj.Select(kv => kv.Key).ToList())
                        await WalkAsync(obj[key]);
                    break;
                case JsonArray arr:
                    foreach (var item in arr.ToList())
                        await WalkAsync(item);
                    break;
            }
        }

        await WalkAsync(node);
        return (count, saved, failed);
    }
}
