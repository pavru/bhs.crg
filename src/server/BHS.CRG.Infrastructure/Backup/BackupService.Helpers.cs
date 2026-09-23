using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Backup;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Чистые помощники копии — часть <see cref="BackupService" />.
///
/// <para>Ничего не знают ни о базе, ни о хранилище: согласование числительных для отчёта, обход
/// JSON в поисках ссылок и путей к блобам, разбор перечислений, накопитель предупреждений.</para>
///
/// <para>⚠️ <b>Тестов на них нет.</b> Все они <c>private static</c>, <c>InternalsVisibleTo</c> в
/// проекте не объявлен, так что снаружи их не позвать. Сказано прямо, потому что первая редакция
/// этого доккомментария утверждала обратное — «проверяются без поднятой базы» (ревью PR #1025).
/// Заявленное покрытие, которого нет, хуже честно названного пробела: именно эти функции решают,
/// что попадёт в копию (<c>ExtractBlobPaths</c>, <c>CollectReferencedDocumentIds</c>), и проверяются
/// они сегодня только косвенно — через прогон копии целиком.</para>
/// </summary>
public partial class BackupService
{
    private static void CollectReferencedDocumentIds(JsonElement element, HashSet<Guid> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Наследование от базового экземпляра: kind = "instance" означает документ.
                if (element.TryGetProperty("_baseRef", out var baseRef) &&
                    baseRef.ValueKind == JsonValueKind.Object &&
                    baseRef.TryGetProperty("kind", out var kind) &&
                    kind.ValueKind == JsonValueKind.String && kind.GetString() == "instance" &&
                    baseRef.TryGetProperty("id", out var baseId) &&
                    baseId.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(baseId.GetString(), out var baseGuid))
                {
                    ids.Add(baseGuid);
                }
                // Протягивание поля из реквизитов другого документа.
                if (element.TryGetProperty("$ref", out var refType) &&
                    refType.ValueKind == JsonValueKind.String &&
                    refType.GetString() is "document" or "instance" &&
                    element.TryGetProperty("instanceId", out var instId) &&
                    instId.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(instId.GetString(), out var instGuid))
                {
                    ids.Add(instGuid);
                }
                foreach (var prop in element.EnumerateObject())
                    CollectReferencedDocumentIds(prop.Value, ids);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectReferencedDocumentIds(item, ids);
                break;
        }
    }

    /// <summary>«1 запись», «2 записи», «5 записей» — счёт в предупреждениях читает человек.</summary>
    private static string Records(int n)
    {
        var tens = n % 100;
        if (tens is >= 11 and <= 14) return $"{n} записей";
        return (n % 10) switch
        {
            1 => $"{n} запись",
            2 or 3 or 4 => $"{n} записи",
            _ => $"{n} записей",
        };
    }

    /// <summary>Согласование сказуемого со счётом: «1 запись ссылается», «2 записи ссылаются».</summary>
    private static string Agree(int n, string singular, string plural) =>
        n % 10 == 1 && n % 100 != 11 ? singular : plural;

    /// <summary>
    /// Тот же счёт, но в родительном падеже — для предлогов, которые его требуют: «у 1 записи»,
    /// «у 2 записей». Именительный <see cref="Records" /> после «у» даёт «у 1 запись».
    /// </summary>
    private static string RecordsGenitive(int n) =>
        n % 10 == 1 && n % 100 != 11 ? $"{n} записи" : $"{n} записей";

    /// <summary>
    /// Служебные заголовки zip на одну запись: локальный заголовок (30 байт) и запись в каталоге
    /// (46), причём имя файла лежит в обоих — отсюда удвоение.
    ///
    /// Круглой константы «сотня байт на запись» тут мало: пути блобов длинные, а имена файлов
    /// кириллические, то есть в UTF-8 вдвое длиннее видимых. На рабочей базе (45 файлов) такая
    /// константа занижала оценку почти на 6 КБ; формула по длине имени сошлась с настоящим архивом
    /// с точностью до десятков байт.
    /// </summary>
    private static long EntryOverhead(string entryName) => 76 + 2L * Encoding.UTF8.GetByteCount(entryName);

    // ── Blob path extraction ──────────────────────────────────────────────────

    private static HashSet<string> ExtractBlobPaths(BackupManifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manifest.CommonDataEntries)
            CollectBlobPaths(e.Data, paths);
        foreach (var e in manifest.CatalogEntities)
            CollectBlobPaths(e.Data, paths);
        // Файлы ассетов шаблонов (issue #403) — графика/шрифты в blob-хранилище.
        foreach (var a in manifest.TemplateAssets ?? [])
            if (!string.IsNullOrEmpty(a.BlobPath)) paths.Add(a.BlobPath);
        // Сканы документов качества (issue #687). Скан — не иллюстрация к документу, а он сам:
        // библиотека без сканов не подтверждает ничего, и переносить её метаданными было бы
        // переносом пустых карточек. Реквизиты обходим тем же сборщиком — там могут лежать
        // вложения (тот же формат, что у реквизитов экземпляра документа).
        foreach (var q in manifest.QualityDocuments ?? [])
        {
            if (!string.IsNullOrEmpty(q.ScanBlobPath)) paths.Add(q.ScanBlobPath);
            CollectBlobPaths(q.Requisites, paths);
        }
        // Проектные данные (issue #833).
        foreach (var d in manifest.Documents ?? [])
        {
            CollectBlobPaths(d.Data, paths);
            foreach (var f in d.GeneratedFiles) paths.Add(f.BlobPath);
        }
        // Файл набора данных - то самое сырьё, из которого документы и собираются. У системных
        // наборов блоба нет вовсе: их сырьё - данные самой системы, а в BlobPath лежит сентинел.
        foreach (var f in manifest.DataSetFiles ?? [])
            if (!string.IsNullOrEmpty(f.BlobPath) && f.Format != "System") paths.Add(f.BlobPath);
        return paths;
    }

    private static void CollectBlobPaths(JsonElement element, HashSet<string> paths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    typeEl.GetString() is "file" or "image" &&
                    element.TryGetProperty("blobPath", out var pathEl) &&
                    pathEl.GetString() is { Length: > 0 } path)
                {
                    paths.Add(path);
                    // И ОРИГИНАЛ картинки, если он есть (issue #534). Уменьшение — производная, и
                    // всё обещание «оригинал сохранён» держится на этом блобе; без него
                    // восстановление из архива оставило бы ссылку на несуществующий файл.
                    if (element.TryGetProperty("originalBlobPath", out var origEl) &&
                        origEl.GetString() is { Length: > 0 } original)
                    {
                        paths.Add(original);
                    }
                }
                else
                {
                    foreach (var prop in element.EnumerateObject())
                        CollectBlobPaths(prop.Value, paths);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectBlobPaths(item, paths);
                break;
        }
    }

    private static string GetContentTypeFromExtension(string ext) =>
        ext.ToLowerInvariant().TrimStart('.') switch
        {
            "pdf"  => "application/pdf",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "xls"  => "application/vnd.ms-excel",
            "png"  => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif"  => "image/gif",
            "webp" => "image/webp",
            "svg"  => "image/svg+xml",
            "ttf"  => "font/ttf",
            "otf"  => "font/otf",
            "ttc"  => "font/collection",
            _ => "application/octet-stream",
        };

    // ── Restore helpers ───────────────────────────────────────────────────────

    private static bool TryParseEnum<TEnum>(
        string value, string what, List<string> warnings, out TEnum parsed) where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, out parsed)) return true;
        warnings.Add($"Пропущена запись: неизвестный {what} «{value}» — копия сделана более новой версией.");
        return false;
    }

    /// <summary>Разделяет записи на «можно восстановить» и «не к чему приложить».</summary>
    private static (List<T> Ok, List<T> Skipped) Split<T>(IEnumerable<T> items, Func<T, bool> canRestore)
    {
        List<T> ok = [], skipped = [];
        foreach (var item in items) (canRestore(item) ? ok : skipped).Add(item);
        return (ok, skipped);
    }

    /// <summary>
    /// Пропущенное — всегда вслух. Молчаливый пропуск строки, у которой не нашлось носителя, и есть
    /// тот самый случай «восстановилось, но не всё», ради которого issue #833 и заведён.
    /// </summary>
    private static void Warn(List<string> warnings, int count, string what, string why)
    {
        if (count == 0) return;
        warnings.Add($"Пропущено {what}: {count} — {why}.");
    }

    private sealed class RestoreStats
    {
        public int PrimitiveTypesCreated, PrimitiveTypesUpdated;
        public int EnumTypesCreated, EnumTypesUpdated;
        public int RecognitionProfilesCreated, RecognitionProfilesUpdated;
        public int DocumentTypesCreated, DocumentTypesUpdated;
        public int TemplatesCreated, TemplatesUpdated;
        public int TemplateAssetsCreated, TemplateAssetsUpdated;
        public bool TypstUserLibRestored;
        public int TypstUserLibFilesRestored;
        public int CatalogEntitiesCreated, CatalogEntitiesUpdated;
        public int CommonDataEntriesCreated, CommonDataEntriesUpdated;
        public int DataSetBindingTemplatesCreated, DataSetBindingTemplatesUpdated;
        public int ReconciliationAliasesCreated, ReconciliationAliasesUpdated;
        public int DataSetProcessingTemplatesCreated, DataSetProcessingTemplatesUpdated;
        public int QualityDocumentsCreated, QualityDocumentsUpdated;

        /// <summary>
        /// Проектные секции (issue #833) — счётчиками по имени, а не восемнадцатью полями подряд.
        /// Отчёт о восстановлении и так перечисляет два десятка чисел; следующая секция копии не
        /// должна означать правку в четырёх местах ради ещё одной пары.
        /// </summary>
        private readonly Dictionary<string, (int Created, int Updated)> _project = [];

        public void Count(string label, int created, int updated)
        {
            if (created == 0 && updated == 0) return;
            _project[label] = (created, updated);
        }

        public IReadOnlyList<RestoreSectionStat>? ProjectSections() => _project.Count == 0
            ? null
            : _project.Select(kv => new RestoreSectionStat(kv.Key, kv.Value.Created, kv.Value.Updated)).ToArray();
    }
}
