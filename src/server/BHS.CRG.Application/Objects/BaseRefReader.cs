using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Разбор ссылки «_baseRef» (базовый экземпляр, issue #71): дискриминированный объект {kind,id}
/// или голая id-строка (legacy). Единый источник правила для резолвера генерации
/// (<c>EntityResolver</c>) и guard'ов удаления — чтобы «на что ссылается base» трактовалось одинаково.
/// </summary>
public static class BaseRefReader
{
    /// <summary>id из значения «_baseRef» ({kind,id} или голая строка), либо null.</summary>
    public static Guid? ParseRef(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return Guid.TryParse(el.GetString(), out var g) ? g : null;
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var gid))
            return gid;
        return null;
    }

    /// <summary>id базового объекта, на который ссылается data через «_baseRef», либо null.</summary>
    public static Guid? GetBaseRefId(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("_baseRef", out var el)) return null;
        return ParseRef(el);
    }

    /// <summary>
    /// Слияние двух JSON-объектов для _baseRef-наследования: базовые поля первыми, собственные
    /// переопределяют их на верхнем уровне; ключ «_baseRef» исключается. Чистая функция — единый
    /// источник для резолвера генерации (<c>EntityResolver</c>) и flatten при копировании (issue #283).
    /// </summary>
    public static JsonElement MergeObjects(JsonElement baseData, JsonElement ownData)
    {
        var merged = new Dictionary<string, JsonElement>();
        if (baseData.ValueKind == JsonValueKind.Object)
            foreach (var p in baseData.EnumerateObject())
                if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        if (ownData.ValueKind == JsonValueKind.Object)
            foreach (var p in ownData.EnumerateObject())
                if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        return JsonSerializer.SerializeToElement(merged);
    }
}

/// <summary>
/// Чтение ссылок «$ref» в значениях полей (issue #269): resolve-объекты
/// {$ref:"catalog", entryId} / {$ref:"document"|"instance", instanceId}, которые EntityResolver
/// разворачивает при генерации. Указывают на другой <c>DomainObject</c> (запись общих данных или
/// документ). Могут лежать на любой глубине Data (вложенные объекты, массивы) — обход рекурсивный.
/// </summary>
public static class RefReader
{
    /// <summary>
    /// id всех объектов, на которые ссылается data через «$ref», рекурсивно.
    /// </summary>
    /// <param name="includeInstanceRefs">
    /// Учитывать ли <c>$ref:"instance"</c> — разворачивание целого документа. Ложно там, где резолвер
    /// зовётся с <c>allowInstanceRefs: false</c> и такую ссылку НЕ разворачивает: внутри реквизитов
    /// документа качества (issue #735) он отдаёт <c>StripRef</c> — стаб из самого узла, в базу за
    /// целью не ходя. Считай её guard ссылкой, документ-цель стал бы неудаляемым из-за указателя,
    /// которым генерация не пользуется. <c>catalog</c> и <c>document</c> разворачиваются и там —
    /// они учитываются всегда.
    /// </param>
    public static IEnumerable<Guid> CollectRefIds(JsonElement el, bool includeInstanceRefs = true)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("$ref", out var rt) && rt.ValueKind == JsonValueKind.String)
                {
                    var refType = rt.GetString();
                    // id живёт в entryId (catalog) либо instanceId (document/instance).
                    var idProp = refType == "catalog" ? "entryId" : "instanceId";
                    if ((includeInstanceRefs || refType != "instance")
                        && el.TryGetProperty(idProp, out var idEl) && Guid.TryParse(idEl.GetString(), out var g))
                        yield return g;
                }
                foreach (var p in el.EnumerateObject())
                    foreach (var id in CollectRefIds(p.Value, includeInstanceRefs)) yield return id;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    foreach (var id in CollectRefIds(item, includeInstanceRefs)) yield return id;
                break;
        }
    }
}

/// <summary>Обратные ссылки между объектами предметной области — для guard'ов удаления.</summary>
public static class DomainObjectReferences
{
    /// <summary>
    /// Кто ссылается на цель — для сообщения отказа. Держатели ссылок живут в ДВУХ таблицах
    /// (<c>domain_objects</c> и <c>quality_documents</c>), а сущность у них общего предка не имеет:
    /// объединяет их не тип, а роль «нашлась ссылка, и удаление цели её оборвёт».
    /// </summary>
    /// <param name="Label">Готовое описание для текста отказа: у документа качества род назван прямо
    /// («документ качества „…“»), иначе одно имя ничего не сказало бы о том, ГДЕ искать ссылку —
    /// библиотека и комплекты редактируются на разных экранах.</param>
    public record Referrer(Guid Id, string Label);

    /// <summary>
    /// Всё, что ссылается на <paramref name="targetId"/>: как на базовый экземпляр («_baseRef»,
    /// issue #71) ИЛИ через «$ref» в значениях полей (issue #269 — doc-ref/@@ref). Сканируются обе
    /// таблицы-держателя ссылок, потому что ссылаться умеют обе и в обе стороны (issue #735):
    /// реквизиты документа качества проходят тот же <c>ResolveNode</c>, что и реквизиты документа
    /// комплекта, — «$ref» в них рабочая ссылка, а не декорация.
    ///
    /// <para><b>Но не всякая.</b> Реквизиты документа качества резолвер обходит с
    /// <c>allowInstanceRefs: false</c>, и <c>$ref:"instance"</c> там не разворачивается — отдаётся
    /// стаб из самого узла, в базу за целью резолвер не ходит. Оберегать эту ссылку значило бы
    /// сделать документ-цель неудаляемым из-за указателя, которым генерация не пользуется, поэтому
    /// с этой стороны учитываются только <c>catalog</c> и <c>document</c>.</para>
    ///
    /// <para>Сканирование в памяти (предикат по JSON не транслируется в SQL); масштаб приложения это
    /// допускает, как и прочие guard'ы удаления. Обе выборки — <c>FindAsync</c>, а не
    /// <c>GetAllAsync</c>: она без отслеживания (см. <c>Repository</c>), и целые таблицы не оседают
    /// в трекере ради проверки, которая ничего не меняет, — превью переноса зовёт этот же скан и не
    /// сохраняет вовсе.</para>
    /// </summary>
    public static async Task<IReadOnlyList<Referrer>> FindReferrersAsync(
        IRepository<DomainObject> objRepo, IRepository<QualityDocument> qualityRepo,
        Guid targetId, CancellationToken ct)
    {
        var objects = await objRepo.FindAsync(_ => true, ct);
        var quality = await qualityRepo.FindAsync(_ => true, ct);

        var found = objects
            .Where(o => o.Id != targetId && ReferencesObject(o.Data.RootElement, targetId))
            .Select(o => new Referrer(o.Id, Name(o.DisplayName)));

        // Документ качества базового экземпляра не имеет — только «$ref» в реквизитах.
        var fromQuality = quality
            .Where(d => d.Id != targetId
                        && RefReader.CollectRefIds(d.Requisites.RootElement, includeInstanceRefs: false)
                            .Contains(targetId))
            .Select(d => new Referrer(d.Id, $"документ качества «{Name(d.DisplayName)}»"));

        return found.Concat(fromQuality).ToList();
    }

    /// <summary>Имя держателя для текста отказа. Пустое имя пропускает и сама библиотека
    /// (<c>EnsureNameFreeAsync</c> считает это заботой валидации формы) — а строка отказа затем
    /// существует ровно для того, чтобы человек нашёл держателя, и ««»» ему в этом не помогает.</summary>
    private static string Name(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? "без имени" : displayName;

    private static bool ReferencesObject(JsonElement data, Guid targetId)
        => BaseRefReader.GetBaseRefId(data) == targetId
           || RefReader.CollectRefIds(data).Contains(targetId);
}

/// <summary>
/// Скраб исходящих ссылок при копировании/переносе документа в ДРУГОЙ комплект (issue #283, стратегия
/// B «умная очистка»): убирает значения-ссылки `$ref:document/instance` — они структурно same-set и в
/// чужом комплекте не резолвятся (дали бы сырой `{$ref}` = мусор в PDF). `$ref:catalog` НЕ трогает
/// (валидность в новом scope проверяется отдельно, для предупреждений). Чистая функция.
///
/// <para><b>«Structurally same-set» перестало быть верным для всех instance-ссылок</b> (issue #733):
/// у них теперь два домена, и второй — библиотека документов качества — видна по цепочке областей, а
/// не по комплекту. Ссылка на документ качества уровня System или стройки в целевом комплекте
/// разрешается штатно, и стереть её значило бы молча выбросить рабочие данные, доложив об этом как
/// об «удалённых ссылках на документы комплекта». Какие идентификаторы уцелеют, решает вызывающий —
/// это вопрос к базе, а класс остаётся чистой функцией.</para>
/// </summary>
public static class RefScrubber
{
    /// <summary>
    /// Очищенная копия data без doc/instance-ссылок + ключи полей верхнего уровня, чьё значение убрано.
    /// </summary>
    /// <param name="keepIds">Идентификаторы, ссылки на которые сохраняются: цели, разрешимые и в новом
    /// расположении (документы качества, видимые из целевого комплекта, — issue #733). Пусто = прежнее
    /// поведение, стираются все.</param>
    public static (JsonElement Data, IReadOnlyList<string> StrippedFields) StripInstanceRefs(
        JsonElement data, IReadOnlySet<Guid>? keepIds = null)
    {
        var stripped = new List<string>();
        var result = Strip(data, topLevel: true, stripped, keepIds)
                     ?? JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());
        return (result, stripped);
    }

    // Возвращает null, если узел САМ — doc/instance-ссылка (должен быть удалён вызывающим).
    private static JsonElement? Strip(JsonElement el, bool topLevel, List<string> stripped, IReadOnlySet<Guid>? keepIds)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsInstanceRef(el) && !IsKept(el, keepIds)) return null;
                var obj = new Dictionary<string, JsonElement>();
                foreach (var p in el.EnumerateObject())
                {
                    var child = Strip(p.Value, topLevel: false, stripped, keepIds);
                    if (child is { } c) obj[p.Name] = c;
                    else if (topLevel && !stripped.Contains(p.Name)) stripped.Add(p.Name);
                }
                return JsonSerializer.SerializeToElement(obj);
            case JsonValueKind.Array:
                var arr = new List<JsonElement>();
                foreach (var item in el.EnumerateArray())
                    if (Strip(item, topLevel: false, stripped, keepIds) is { } c) arr.Add(c);
                return JsonSerializer.SerializeToElement(arr);
            default:
                return el.Clone();
        }
    }

    private static bool IsInstanceRef(JsonElement el)
        => el.TryGetProperty("$ref", out var r) && r.ValueKind == JsonValueKind.String
           && r.GetString() is "document" or "instance";

    private static bool IsKept(JsonElement el, IReadOnlySet<Guid>? keepIds)
        => keepIds is { Count: > 0 }
           && el.TryGetProperty("instanceId", out var idEl)
           && Guid.TryParse(idEl.GetString(), out var id)
           && keepIds.Contains(id);
}
