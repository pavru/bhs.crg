using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Storage;

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

    /// <summary>
    /// Файлы хранилища, которые копия знает по месту: список, выписанный руками. Архив, оценка веса и
    /// паспорт берут пути не отсюда, а из <see cref="SelectBlobs" />: к списку там добавляется обход
    /// манифеста по форме пути — он находит то, о чём список не знает (issue #1422).
    /// </summary>
    /// <param name="warnings">Куда сказать о данных, пути из которых прочесть не удалось.</param>
    private static HashSet<string> ExtractBlobPaths(BackupManifest manifest, List<string> warnings)
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
        {
            if (!string.IsNullOrEmpty(f.BlobPath) && f.Format != "System") paths.Add(f.BlobPath);
            // Части альбома по ГОСТ (issue #1399): каждая группа-документ режется в свой под-PDF, и
            // путь к нему лежит в разбиении набора. До #1399 копия их не уносила вовсе — отчёт писал
            // «восстановлено N из N», а генерация после восстановления молча выдавала документы без
            // вложений: части в реестре файлов уже не значились.
            CollectGroupingParts(f, paths, warnings);
        }
        // Те же части глазами реестра «Документы»: генерация берёт путь вложения ИМЕННО отсюда —
        // колонкой ФайлПуть, через файловый маппинг привязки, — а не из разбиения. Сегодня строки —
        // проекция разбиения, и пути совпадают. Но наборы, разрезанные до issue #38, несут путь только
        // в строках, а путь из строк, которого нет в хранилище, — это вложение, которое генерация уже
        // не найдёт: предупреждение экспорта о нём правдиво, а не ложная тревога.
        //
        // Только реестр «Документы»: колонку с тем же именем может нести любой загруженный набор
        // (Excel, CSV), и его значения — текст пользователя, а не пути нашего хранилища.
        foreach (var s in manifest.DataSetSources ?? [])
            if (s.SheetOrPath == PdfProfiles.GostDocumentsMarker)
                CollectRowFilePaths(s, paths, warnings);
        // Файлы модулей (issue #1261): сканы счетов. Перечень собран при снятии строк модуля —
        // IModuleSchemaBackup: колонки модуля ядро не знает, а путь в его строках находит обход. Сюда
        // он приходит готовым, чтобы архив, оценка веса и паспорт брали пути из одного места.
        foreach (var m in manifest.ModuleData ?? [])
            paths.UnionWith(m.Files ?? []);
        return paths;
    }

    /// <summary>
    /// Пути частей из разбиения набора — тем же классом, что режет и убирает сами части
    /// (<see cref="GostGroupingSerialization" />): где в разбиении лежат части, знает одно место.
    /// Читаются только пути (<see cref="GostGroupingSerialization.ReadPartPaths" />), а не разбиение
    /// целиком: у больших альбомов в нём мегабайты распознанных таблиц (ревью PR #1408).
    ///
    /// <para>Непрочитанное разбиение не роняет копию: отказом стала бы копия ВСЕЙ системы из-за одного
    /// набора. Отказ здесь устроен значением, а не исключением, — фильтр исключений по типам уже раз
    /// пропустил разбиение чужой формы (<c>Groups: null</c>) сквозь себя и ронял копию целиком. Но и не
    /// молчит — набор назван в предупреждениях, с выходом. Сам файл набора уезжает как прежде, а части
    /// из него вырезаются заново.</para>
    /// </summary>
    private static void CollectGroupingParts(BackupDataSetFile file, HashSet<string> paths, List<string> warnings)
    {
        if (GostGroupingSerialization.ReadPartPaths(file.Grouping) is { } parts)
            paths.UnionWith(parts);
        else
            warnings.Add($"Набор данных «{file.Name}»: разбиение на документы не прочитано, и вырезанные " +
                         "из него PDF документов в копию не вошли. Сам файл набора в копии есть: после " +
                         "восстановления сохраните разбиение набора — части вырежутся заново.");
    }

    /// <summary>
    /// Пути из строк реестра «Документы» — значения колонки
    /// <see cref="GostGroupingProjection.FilePathColumn" />. Строка без колонки или с непривычным
    /// значением путей не даёт; непрочитанный кэш назван в предупреждениях, как и разбиение.
    ///
    /// <para>Берётся только точный путь нашего хранилища (<see cref="BlobPathShape.IsExactPath" />),
    /// ревью PR #1408; общая форма <see cref="BlobPathShape.Pattern" /> для этого слишком широка
    /// (ревью PR #1289, issue #1422). У группы, которую не удалось вырезать, проекция оставляет
    /// распознанные поля штампа, и поле профиля с тем же именем кладёт в колонку текст из PDF. Взятый «путём», он
    /// ушёл бы в хранилище запросом за чужим объектом — или ложным «файлов не оказалось», в котором
    /// тонет настоящая потеря.</para>
    /// </summary>
    private static void CollectRowFilePaths(BackupDataSetSource source, HashSet<string> paths, List<string> warnings)
    {
        if (string.IsNullOrEmpty(source.CachedData)) return;
        try
        {
            using var doc = JsonDocument.Parse(source.CachedData);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object &&
                    row.TryGetProperty(GostGroupingProjection.FilePathColumn, out var path) &&
                    path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } part &&
                    BlobPathShape.IsExactPath(part))
                    paths.Add(part);
        }
        catch (JsonException)
        {
            warnings.Add($"Источник «{source.Name}»: строки реестра документов не прочитаны, и пути " +
                         "вложений из них в копию не вошли.");
        }
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
