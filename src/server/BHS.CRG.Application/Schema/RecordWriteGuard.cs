using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Охрана записи (issue #957, ТЗ CORE-20): при сохранении проверяются ВИД ЗНАЧЕНИЯ и ЗАПЕРТЫЕ ПОЛЯ.
/// Из того же обходчика, что и аудит (<see cref="SchemaDataAuditor"/>), — правило одно, разные у
/// них только последствия: аудит рассказывает, охрана отказывает.
///
/// <para><b>Обязательности здесь нет, и это не упущение.</b> Она проверяется на ПЕРЕХОДЕ, который
/// объявляет модуль («подан», «разобран», «выдана на подпись»), а не при сохранении: иначе
/// наполовину распознанный счёт и черновик отчёта сохранить было бы нельзя. Сама проверка живёт в
/// <see cref="Generation.ResolutionScanner.ScanMissingRequired"/> и работает по РАЗРЕШЁННОМУ
/// контексту (реквизиты + привязки + база + дефолты), а не по сырым данным.</para>
///
/// <para><b>⚠️ Отказ выносится только тому, что эта запись ВНОСИТ.</b> Проверять документ целиком
/// нельзя: кривые значения в базе уже есть (ради них написано приведение аудита, issue #643), и
/// охрана «целиком» сделала бы такую запись нередактируемой — открыл, нажал «Сохранить» без
/// правок, получил отказ, и починить из того же экрана нечем. Тот же довод уже записан рядом, у
/// проверки уникальности имени документа качества (issue #588).</para>
///
/// <para><b>Сравнение — по паре «ключ поля + сырое значение», а не по пути.</b> Путь
/// (<c>Работы[3].Количество</c>) ломается на вставке строки в таблицу: вставили строку сверху — все
/// нижние «изменились», и старые кривые значения начали бы отказывать на ровном месте. Цена вслух:
/// СКОПИРОВАТЬ уже лежащее кривое значение в соседнюю строку охрана не заметит. Это дыра, и она
/// осознанная: случай виден аудиту, и он несравнимо дешевле нередактируемой записи.</para>
///
/// <para><b>Осиротевший ключ (<see cref="SchemaDataAuditor.OrphanKey"/>) охраной не считается.</b>
/// «Аудит → охрана» читается как «теперь всякая находка — отказ», и это неверно: ключ вне схемы
/// при сохранении — нормальное состояние между правкой схемы и переносом данных, а отказ по нему
/// закрыл бы сам путь починки.</para>
///
/// <para><b>Кто охраняется.</b> Перечень поимённый, потому что «закрыл один вход из нескольких» —
/// наша повторяющаяся ошибка:</para>
/// <list type="bullet">
/// <item>реквизиты документа (<c>UpdateRequisitesCommand</c>);</item>
/// <item>запись общих данных — создание и правка (<c>CommonDataHandlers</c>), правка проверяется
/// ПОСЛЕ слияния с привязками наборов: проверь мы тело запроса, привязка пронесла бы мимо охраны
/// что угодно;</item>
/// <item>документ качества — создание и правка (<c>QualityDocHandlers</c>);</item>
/// <item>реквизиты, прочитанные из печатной формы (<c>PrintFormEndpoints</c>) — он кладёт значения
/// «как прочитал».</item>
/// </list>
/// <para><b>Кто НЕ охраняется, и почему у каждого своя причина:</b></para>
/// <list type="bullet">
/// <item><c>ApplyAuditFixesCommand</c> — его работа и есть трогать кривые данные; охрана сделала бы
/// битую запись непочинимой;</item>
/// <item><c>MigrateFieldKeyCommand</c> — переносит существующие значения по всем экземплярам сразу,
/// и отказ из-за одной старой записи оборвал бы правку схемы на середине;</item>
/// <item>восстановление резервной копии — возвращает то, что было; копия, которая не
/// восстанавливается, хуже любой кривизны;</item>
/// <item>фиксапы и миграции данных в Infrastructure — та же причина;</item>
/// <item>штамп метаданных при генерации — отказ на последнем шаге выпуска из-за чужого старого
/// значения; от вида значения освобождён, а запертых полей он не пишет.</item>
/// </list>
/// </summary>
public static class RecordWriteGuard
{
    /// <summary>Код отказа «запертое поле тронуто».</summary>
    public const string LockedField = "locked-field";

    /// <summary>
    /// Чем запись запрещена — находками С ПУТЯМИ, а не склеенной строкой: на клиенте уже есть
    /// раскладка находок по пути (issue #644), и без адреса «отказ с указанием поля» превратился бы
    /// в баннер, по которому нарушение внутри строки таблицы не найти. Пусто — запись разрешена.
    /// </summary>
    /// <param name="stored">Данные, КАК ОНИ ЛЕЖАТ сейчас. Для создания — пустой объект.</param>
    /// <param name="incoming">Данные, которые просят сохранить.</param>
    public static IReadOnlyList<AuditIssue> Refusals(
        JsonElement stored, JsonElement incoming, Guid typeId,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById)
    {
        // Уровень правки схемы — по букве ТЗ CORE-20: охраняются «расширяемый» и «закрытый».
        // Следствие принято сознательно: пока модулей, заводящих типы, нет, охрана не срабатывает
        // ни на одном живом типе, и проверена она построчно тестами.
        if (EffectiveLevel(typeId, byId) == SchemaEditLevel.Open) return [];

        var refusals = new List<AuditIssue>();
        var incomingNode = JsonNode.Parse(incoming.GetRawText());
        var storedNode = JsonNode.Parse(stored.GetRawText());

        // ── Вид значения: только то, чего в сохранённом документе не было ──────
        var already = ScalarsByKey(stored);
        foreach (var issue in SchemaDataAuditor.Audit(incoming, typeId, byId, primitivesById))
        {
            if (issue.Code == SchemaDataAuditor.OrphanKey) continue;
            var raw = incomingNode is null ? null : JsonPathEditor.ValueAt(incomingNode, issue.Path)?.ToJsonString();
            if (raw is not null && already.TryGetValue(LeafKey(issue.Path), out var seen) && seen.Contains(raw))
                continue; // значение не ново — с ним запись жила и дальше живёт, это забота аудита
            refusals.Add(issue with { Severity = AuditSeverity.Error });
        }

        // ── Запертые поля: бит-в-бит, как лежит ───────────────────────────────
        CheckLocks(storedNode, incomingNode, typeId, byId, "", refusals);
        return refusals;
    }

    /// <summary>
    /// Уровень, по которому судят охрану, — САМЫЙ СТРОГИЙ в цепочке наследования, а не собственный.
    /// Иначе замок снимался бы наследованием: производный тип заводит администратор, он всегда
    /// открытый, а поля (и замки) родителя-модуля в нём остаются.
    /// </summary>
    public static SchemaEditLevel EffectiveLevel(Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var level = SchemaEditLevel.Open;
        var cur = byId.GetValueOrDefault(typeId);
        var guard = 0;
        while (cur is not null && guard++ < 32)
        {
            if (cur.EditLevel > level) level = cur.EditLevel;
            cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
        }
        return level;
    }

    /// <summary>
    /// Запертые поля типа на этом уровне вложенности. Рекурсия — только в ОДИНОЧНОЕ составное поле:
    /// у строки таблицы личности нет (по индексу её не опознать после вставки), и запертые поля
    /// внутри строк появятся вместе с первым модулем, которому они понадобятся. Ссылка
    /// (<c>doc-ref</c>) — указатель, а не данные: внутрь неё не идём.
    /// </summary>
    private static void CheckLocks(
        JsonNode? stored, JsonNode? incoming, Guid typeId,
        IReadOnlyDictionary<Guid, DocumentType> byId, string basePath, List<AuditIssue> refusals)
    {
        foreach (var f in DocumentTypeSchemaReader.EffectiveFields(typeId, byId))
        {
            var path = basePath.Length == 0 ? f.Key : $"{basePath}.{f.Key}";
            var was = Child(stored, f.Key);
            var now = Child(incoming, f.Key);

            if (f.Locked)
            {
                if (!JsonNode.DeepEquals(was, now))
                    refusals.Add(new AuditIssue(LockedField, AuditSeverity.Error, path,
                        $"Поле «{f.Title ?? f.Key}» заперто: его значение кладёт код модуля. " +
                        Changed(was, now)));
                continue;
            }

            if (f.Type == "complex" && f.TypeId is { } tid && byId.ContainsKey(tid))
                CheckLocks(was, now, tid, byId, path, refusals);
        }
    }

    /// <summary>
    /// Что именно случилось с запертым полем. Стирание называется отдельно: молча потерянное
    /// значение — самый частый способ испортить запись (клиент, пересобирающий объект поимённо,
    /// теряет свойство, которого не назвал, — ровно дефект ревью PR #1004).
    /// </summary>
    private static string Changed(JsonNode? was, JsonNode? now) => (was, now) switch
    {
        (not null, null) => "Значение стёрто — верните его в запись как есть.",
        (null, not null) => "Значение заполнено впервые — это делает модуль своей командой.",
        _ => "Значение изменено — верните прежнее.",
    };

    private static JsonNode? Child(JsonNode? node, string key)
        => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>
    /// Все скалярные значения документа: ключ поля → множество сырых записей. Ключ — ЛИСТ пути
    /// (без индексов), потому что по этому же ключу сравнивается находка: имя поля переживает
    /// вставку строки в таблицу, а номер строки — нет.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ScalarsByKey(JsonElement data)
    {
        var acc = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        Walk(data, "", acc);
        return acc;

        static void Walk(JsonElement node, string key, Dictionary<string, HashSet<string>> acc)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in node.EnumerateObject()) Walk(p.Value, p.Name, acc);
                    break;
                case JsonValueKind.Array:
                    // Элементы массива держат ключ самого поля: строка таблицы адреса не добавляет.
                    foreach (var item in node.EnumerateArray()) Walk(item, key, acc);
                    break;
                default:
                    if (key.Length == 0) break;
                    if (!acc.TryGetValue(key, out var set)) acc[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(node.GetRawText());
                    break;
            }
        }
    }

    /// <summary>Ключ поля из пути находки: <c>Работы[3].Количество</c> → <c>Количество</c>.</summary>
    private static string LeafKey(string path)
    {
        var tail = path[(path.LastIndexOf('.') + 1)..];
        var bracket = tail.IndexOf('[');
        return bracket < 0 ? tail : tail[..bracket];
    }
}
