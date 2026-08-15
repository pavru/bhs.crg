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
    /// <para><b>Кого разбирать, спрашиваем у базы</b> (issue #745). Предикат по JSON в SQL не
    /// транслируется, и раньше обе таблицы читались ЦЕЛИКОМ, а решение принималось в памяти —
    /// приём законный при малых данных, но проверка стоит перед каждым удалением и растёт вместе с
    /// общими данными. Теперь <see cref="IReferenceIndex" /> отбирает строки, где идентификатор
    /// вообще упоминается, а разбирается только этот десяток. Само правило «что считать ссылкой»
    /// осталось здесь, в C#, и ни одна его тонкость в SQL не уехала: индекс лишь СУЖАЕТ.</para>
    ///
    /// <para>Выборка — <c>FindAsync</c>, а не <c>GetAllAsync</c>: она без отслеживания
    /// (см. <c>Repository</c>), и найденное не оседает в трекере ради проверки, которая ничего не
    /// меняет, — превью переноса зовёт этот же скан и не сохраняет вовсе.</para>
    /// </summary>
    public static Task<IReadOnlyList<Referrer>> FindReferrersAsync(
        IRepository<DomainObject> objRepo, IRepository<QualityDocument> qualityRepo,
        IReferenceIndex index, Guid targetId, CancellationToken ct)
        => FindReferrersAsync(objRepo, qualityRepo, index, new HashSet<Guid> { targetId }, ct);

    /// <summary>
    /// То же для ГРУППЫ целей — каскадное удаление уровня (issue #739): удаляя комплект, раздел или
    /// стройку, мы уносим все объекты поддерева разом, и спрашивать про каждый отдельно значило бы
    /// сканировать обе таблицы по разу на объект.
    ///
    /// <para><b>Держатель из самой группы не считается</b> — и это не оптимизация, а суть проверки.
    /// Документ комплекта, ссылающийся на соседний документ того же комплекта, уходит вместе с ним:
    /// ссылка исчезает целиком, а не повисает. Блокируй мы и такие, комплект с двумя связанными
    /// документами стал бы неудаляемым навсегда — распутать это можно было бы только правкой
    /// реквизитов вручную. Одиночный случай — частный: цель не блокирует сама себя.</para>
    /// </summary>
    public static async Task<IReadOnlyList<Referrer>> FindReferrersAsync(
        IRepository<DomainObject> objRepo, IRepository<QualityDocument> qualityRepo,
        IReferenceIndex index, IReadOnlySet<Guid> targetIds, CancellationToken ct)
    {
        if (targetIds.Count == 0) return [];

        var (objects, quality) = await CandidatesAsync(objRepo, qualityRepo, index, targetIds, ct);

        var found = objects
            .Where(o => !targetIds.Contains(o.Id) && ReferencesAny(o.Data.RootElement, targetIds))
            .Select(o => new Referrer(o.Id, Name(o.DisplayName)));

        // Документ качества базового экземпляра не имеет — только «$ref» в реквизитах.
        var fromQuality = quality
            .Where(d => !targetIds.Contains(d.Id)
                        && RefReader.CollectRefIds(d.Requisites.RootElement, includeInstanceRefs: false)
                            .Any(targetIds.Contains))
            .Select(d => new Referrer(d.Id, $"документ качества «{Name(d.DisplayName)}»"));

        return found.Concat(fromQuality).ToList();
    }

    /// <summary>
    /// Кандидаты в держатели: строки, где идентификаторы вообще упоминаются. Настоящую ссылку среди
    /// них ищет вызывающий — сужение и решение сознательно разнесены (см. <see cref="IReferenceIndex" />).
    /// </summary>
    private static async Task<(IReadOnlyList<DomainObject> Objects, IReadOnlyList<QualityDocument> Quality)>
        CandidatesAsync(
            IRepository<DomainObject> objRepo, IRepository<QualityDocument> qualityRepo,
            IReferenceIndex index, IReadOnlySet<Guid> targetIds, CancellationToken ct)
    {
        var ids = targetIds.ToList();
        var objectIds = await index.ObjectsMentioningAsync(ids, ct);
        var qualityIds = await index.QualityDocumentsMentioningAsync(ids, ct);

        // Пустой список — не повод идти в базу за «ничем»: EF всё равно построил бы запрос.
        var objects = objectIds.Count == 0
            ? (IReadOnlyList<DomainObject>)[]
            : await objRepo.FindAsync(o => objectIds.Contains(o.Id), ct);
        var quality = qualityIds.Count == 0
            ? (IReadOnlyList<QualityDocument>)[]
            : await qualityRepo.FindAsync(d => qualityIds.Contains(d.Id), ct);

        return (objects, quality);
    }

    /// <summary>Имя держателя для текста отказа. Пустое имя пропускает и сама библиотека
    /// (<c>EnsureNameFreeAsync</c> считает это заботой валидации формы) — а строка отказа затем
    /// существует ровно для того, чтобы человек нашёл держателя, и ««»» ему в этом не помогает.</summary>
    private static string Name(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? "без имени" : displayName;

    /// <summary>
    /// Какие из <paramref name="candidateIds"/> ещё держат ссылками записи ВНЕ этого множества.
    ///
    /// <para>Обратная задача к <see cref="FindReferrersAsync(IRepository{DomainObject},
    /// IRepository{QualityDocument}, IReferenceIndex, IReadOnlySet{Guid}, CancellationToken)"/>: там спрашивают «кто
    /// держит», здесь — «что держат». Нужна уборке осиротевших записей (issue #739): сирота не
    /// обязательно мусор — её мог оставить прежний каскад, а ссылка на неё резолвится по
    /// идентификатору и работает по сей день. Удали такую — рабочая ссылка стала бы висячей, то
    /// есть уборка своими руками сделала бы то, ради предотвращения чего вся эта работа.</para>
    ///
    /// <para>Держатель, сам входящий в множество, не в счёт: «сирота ссылается на сироту» —
    /// замкнутый остаток, держать его вечно незачем.</para>
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> FindHeldTargetsAsync(
        IRepository<DomainObject> objRepo, IRepository<QualityDocument> qualityRepo,
        IReferenceIndex index, IReadOnlySet<Guid> candidateIds, CancellationToken ct)
    {
        if (candidateIds.Count == 0) return new HashSet<Guid>();

        var (objects, quality) = await CandidatesAsync(objRepo, qualityRepo, index, candidateIds, ct);

        var held = new HashSet<Guid>();
        foreach (var o in objects)
        {
            if (candidateIds.Contains(o.Id)) continue;
            if (BaseRefReader.GetBaseRefId(o.Data.RootElement) is { } b && candidateIds.Contains(b)) held.Add(b);
            foreach (var id in RefReader.CollectRefIds(o.Data.RootElement))
                if (candidateIds.Contains(id)) held.Add(id);
        }
        foreach (var d in quality)
        {
            if (candidateIds.Contains(d.Id)) continue;
            foreach (var id in RefReader.CollectRefIds(d.Requisites.RootElement, includeInstanceRefs: false))
                if (candidateIds.Contains(id)) held.Add(id);
        }
        return held;
    }

    private static bool ReferencesAny(JsonElement data, IReadOnlySet<Guid> targetIds)
        => (BaseRefReader.GetBaseRefId(data) is { } baseId && targetIds.Contains(baseId))
           || RefReader.CollectRefIds(data).Any(targetIds.Contains);
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
