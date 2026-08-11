using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// C#-аналог NewElementResolverStyles.xsl: разрешает $ref-объекты в реквизитах,
/// подмешивает данные сущностей каталога в контекст генерации.
/// </summary>
public class EntityResolver(AppDbContext db, IExpressionEvaluator expressionEvaluator) : IEntityResolver
{
    // Предел ЦЕПОЧКИ ССЫЛОК: сколько переходов ref→ref разворачиваем подряд. Считается только на
    // переходах, а не на вложенности JSON (issue #723): раньше счётчик был один на оба, и каждая
    // развёрнутая ссылка съедала две единицы (заход внутрь резолвнутого объекта + обход его полей),
    // из-за чего живая цепочка «строка источника → документ → персона → приказ → организация»
    // упиралась в предел на последнем звене, а сканер выдавал обрыв за удалённую запись.
    // Ортогонален allowInstanceRefs (защита от циклов по документам).
    private const int MaxRefDepth = 8;

    // Предел вложенности САМОГО JSON — защита от патологически глубоких структур (рекурсия обхода).
    // Отдельный и с большим запасом: обычные реквизиты в него не упираются, и расходовать на него
    // бюджет ссылок незачем.
    private const int MaxNodeDepth = 64;

    public async Task<GenerationContext> ResolveAsync(DocumentView instance, bool keepRefProvenance = false,
        CancellationToken ct = default)
    {
        // Наследование от базового экземпляра (issue #71): если реквизиты несут "_baseRef", подмешиваем
        // реквизиты базы — документа комплекта ЛИБО записи общих данных (полиморфно, по скоп-близости) —
        // собственные поля переопределяют. Только Requisites; PluginData (per-instance кэш плагинов)
        // не наследуется. Резолв-тайм — ничего не персистим. visited стартует с текущего инстанса.
        GenerationContext ctx;
        var reqRoot = instance.Requisites.RootElement;
        if (reqRoot.ValueKind == JsonValueKind.Object && reqRoot.TryGetProperty("_baseRef", out _))
        {
            var scope = await ScopeChains.LoadAsync(db, instance.DocumentSetId, ct);
            var effReq = await ResolveObjectBaseRefAsync(reqRoot, scope, [instance.Id], ct);
            using var effReqDoc = JsonSerializer.SerializeToDocument(effReq);
            ctx = GenerationContext.FromJson(effReqDoc, instance.PluginData);
        }
        else
        {
            ctx = GenerationContext.FromJson(instance.Requisites, instance.PluginData);
        }

        // Профили уровней (issue #258) — амбиентные данные стройки/раздела/комплекта под ключом
        // data.уровень.{стройка,раздел,комплект}. ДО resolve-refs: вложенные $ref/_baseRef профилей
        // резолвятся тем же проходом ниже.
        await InjectScopeProfilesAsync(ctx, instance.DocumentSetId, ct);

        await ResolveContextRefsAsync(ctx, instance.DocumentSetId, keepRefProvenance, ct);
        return ctx;
    }

    /// <summary>
    /// Инжектит профили уровней в контекст под ключом «уровень» = { стройка, раздел, комплект } (issue #258).
    /// Read-only: находит объект-профиль (профиль-тип по тэгу × scope контейнера), резолвит его через
    /// <see cref="ResolveEntryByIdAsync"/> (с _baseRef-мержем); отсутствующий уровень → пустой объект
    /// (не отсутствие ключа — чтобы шаблонам было проще). Каскад: доступны все три уровня-предка документа.
    /// </summary>
    private async Task InjectScopeProfilesAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct)
    {
        var scope = await ScopeChains.LoadAsync(db, documentSetId, ct);
        var types = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var empty = JsonDocument.Parse("{}").RootElement.Clone();

        var profiles = new Dictionary<string, JsonElement>();
        foreach (var (level, tag, key) in LevelProfiles.Levels)
        {
            var containerId = level switch
            {
                CatalogScope.Construction => scope.ConstructionId,
                CatalogScope.Section => scope.SectionId,
                CatalogScope.Set => scope.SetId,
                _ => Guid.Empty,
            };
            var value = empty;
            if (containerId != Guid.Empty && LevelProfiles.ResolveProfileTypeId(types, tag) is { } typeId)
            {
                var profileObj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(
                    o => o.ScopeLevel == level && o.ScopeId == containerId && o.CompositeTypeId == typeId, ct);
                if (profileObj is not null)
                    value = await ResolveEntryByIdAsync(profileObj.Id, scope, [], ct);
            }
            profiles[key] = value;
        }
        ctx.Set("уровень", JsonSerializer.SerializeToElement(profiles));
    }

    public Task ResolveContextRefsAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct = default)
        => ResolveContextRefsAsync(ctx, documentSetId, keepRefProvenance: false, ct);

    private async Task ResolveContextRefsAsync(GenerationContext ctx, Guid documentSetId,
        bool keepRefProvenance, CancellationToken ct)
    {
        var scope = await ScopeChains.LoadAsync(db, documentSetId, ct);
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (ctx.Data[key] is not JsonElement el) continue;
            ctx.Set(key, await ResolveNode(el, scope, refDepth: 0, nodeDepth: 0, allowInstanceRefs: true,
                keepRefProvenance, ct));
        }
    }

    public async Task ApplyDefaultsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
    {
        var allDocTypes = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.DocumentTypeId, allDocTypes);
        foreach (var f in fields)
        {
            if (f.DefaultValue is null) continue;
            if (!SchemaFieldKinds.IsScalar(f.Type)) continue; // complex/array/doc-ref/doc-array/file/image — не трогаем
            if (ctx.Data.ContainsKey(f.Key)) continue; // уже задано инстансом или биндингом — не перезаписываем
            ctx.Set(f.Key, f.DefaultValue.Value);
        }
    }

    /// <summary>
    /// Резолвит enum-поля реквизитов из кода в отображаемое имя перед генерацией (issue #59): в
    /// реквизитах хранится стабильный код EnumType.Values, но в PDF должен попасть человекочитаемый
    /// текст. Scope сознательно ограничен верхнеуровневыми скалярными полями — то же ограничение,
    /// что уже есть у ApplyDefaultsAsync (не резолвит внутрь строк array/complex полей). Толерантно:
    /// код без совпадения в Options остаётся как есть (та же философия, что и везде в резолвере).
    /// </summary>
    public async Task ResolveEnumLabelsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
    {
        var allDocTypes = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var enumTypesById = await db.EnumTypes.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.DocumentTypeId, allDocTypes, enumTypesById);
        foreach (var f in fields)
        {
            if (f.Type != "enum" || f.Options is null || f.Options.Count == 0) continue;
            if (!ctx.Data.TryGetValue(f.Key, out var raw)) continue;
            // ctx.Data хранит JsonElement (реквизиты из FromJson) — но допускаем и обычную строку
            // (напр. если значение положено напрямую, не через JSON-парсинг).
            var code = raw switch
            {
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                string s => s,
                _ => null,
            };
            if (code is null) continue;
            var match = f.Options.FirstOrDefault(o => o.Code == code);
            if (match is not null) ctx.Set(f.Key, match.Label);
        }
    }

    /// <summary>
    /// Вычисляет расчётные поля верхнего уровня (issue #368) через <see cref="ComputedFieldResolver"/>:
    /// движок делегирован <see cref="IExpressionEvaluator"/> (Jint), обход/топосорт/диагностика — в
    /// Application-резолвере. enumTypesById передаём, чтобы формула видела enum-siblings как label.
    /// </summary>
    public async Task ResolveComputedFieldsAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic> diagnostics, CancellationToken ct = default)
    {
        // Фаза 2 (#370): весь тип-граф — верхний уровень + вложенные составные, каждый в своём скоупе.
        var allDocTypes = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        ComputedFieldResolver.ResolveTree(ctx, instance.DocumentTypeId, allDocTypes, expressionEvaluator, diagnostics);
    }

    /// <summary>
    /// Единая точка разбора $ref: обходит произвольное JSON-дерево (объекты/массивы на любой
    /// глубине — раньше это были три разошедшиеся копии). <paramref name="refDepth"/> — длина уже
    /// пройденной цепочки ссылок, <paramref name="nodeDepth"/> — вложенность самого JSON (пределы
    /// раздельные, см. константы); <paramref name="allowInstanceRefs"/> — разрешено ли на этом шаге
    /// разворачивать instance-ссылки (становится false внутри уже развёрнутого instance — один
    /// переход по документу, защита от циклов A→B→C).
    /// </summary>
    private async Task<JsonElement> ResolveNode(JsonElement node, ScopeChain scope, int refDepth, int nodeDepth,
        bool allowInstanceRefs, bool keepRefProvenance, CancellationToken ct)
    {
        if (nodeDepth >= MaxNodeDepth) return MarkIfRef(node);

        switch (node.ValueKind)
        {
            case JsonValueKind.Object when node.TryGetProperty("$ref", out var refTypeProp):
                // Бюджет цепочки исчерпан — ссылку оставляем сырой, но помечаем ПОЧЕМУ: иначе сканер
                // прочтёт её как ссылку на удалённую запись и пошлёт искать то, чего не терялось.
                return refDepth >= MaxRefDepth
                    ? MarkDepthLimited(node)
                    : await ResolveRefObject(node, refTypeProp.GetString(), scope, refDepth, nodeDepth,
                        allowInstanceRefs, keepRefProvenance, ct);

            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, JsonElement>();
                foreach (var prop in node.EnumerateObject())
                    dict[prop.Name] = await ResolveNode(prop.Value, scope, refDepth, nodeDepth + 1,
                        allowInstanceRefs, keepRefProvenance, ct);
                return JsonSerializer.SerializeToElement(dict);
            }

            case JsonValueKind.Array:
            {
                var list = new List<JsonElement>();
                foreach (var item in node.EnumerateArray())
                    list.Add(await ResolveNode(item, scope, refDepth, nodeDepth + 1, allowInstanceRefs,
                        keepRefProvenance, ct));
                return JsonSerializer.SerializeToElement(list);
            }

            default:
                return node.Clone();
        }
    }

    /// <summary>Пометка «дальше не пошли сами» — только на $ref-узле (остальное не ссылка).</summary>
    private static JsonElement MarkIfRef(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("$ref", out _)
            ? MarkDepthLimited(node)
            : node.Clone();

    /// <summary>Сырая ссылка + причина отказа (issue #723) — её читает <c>ResolutionScanner</c>.</summary>
    private static JsonElement MarkDepthLimited(JsonElement node)
        => WithMeta(node, RefUnresolved.Key, RefUnresolved.DepthLimit);

    private async Task<JsonElement> ResolveRefObject(JsonElement node, string? refType, ScopeChain scope,
        int refDepth, int nodeDepth, bool allowInstanceRefs, bool keepRefProvenance, CancellationToken ct)
    {
        switch (refType)
        {
            // Запись каталога (с _baseRef-наследованием) → заходим внутрь, её собственные ссылки тоже разворачиваем.
            case "catalog"
                when node.TryGetProperty("entryId", out var entryIdProp) && Guid.TryParse(entryIdProp.GetString(), out var entryId):
            {
                var resolved = await ResolveEntryByIdAsync(entryId, scope, [], ct);
                if (resolved.ValueKind == JsonValueKind.Undefined) return node.Clone();
                var recursed = await ResolveNode(resolved, scope, refDepth + 1, nodeDepth + 1, allowInstanceRefs,
                    keepRefProvenance, ct);
                // Фактический тип записи каталога (issue #344) — для корректного instance-of на подтипах
                // (поле «Организация», запись «Подрядчик»). Application развернёт _typeId в _type.
                var typeId = await db.DomainObjects.Where(o => o.Id == entryId)
                    .Select(o => (Guid?)o.CompositeTypeId).FirstOrDefaultAsync(ct);
                var stamped = typeId is { } tid ? WithTypeId(recursed, tid) : recursed;
                // Чья это карточка (issues #594, #595) — только по явной просьбе внешнего чтения.
                return keepRefProvenance
                    ? WithMeta(stamped, RefProvenance.EntryIdKey, entryId.ToString())
                    : stamped;
            }

            // Протягивание одного поля из реквизитов другого документа — значение как есть, без рекурсии
            // (сохраняем текущее поведение). Работает на любой глубине — это чинит баг A (ссылка в массиве).
            case "document"
                when node.TryGetProperty("instanceId", out var instIdProp) && Guid.TryParse(instIdProp.GetString(), out var instId)
                     && node.TryGetProperty("fieldKey", out var fieldKeyProp):
            {
                var fieldKey = fieldKeyProp.GetString() ?? string.Empty;
                var refObj = await db.DomainObjects.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == instId && o.ScopeLevel == CatalogScope.Set && o.ScopeId == scope.SetId, ct);
                return refObj is not null && refObj.Data.RootElement.TryGetProperty(fieldKey, out var fieldVal)
                    ? fieldVal.Clone()
                    : node.Clone();
            }

            // Разворачивание другого документа — один раз (allowInstanceRefs → false внутри).
            case "instance"
                when allowInstanceRefs
                     && node.TryGetProperty("instanceId", out var docInstIdProp) && Guid.TryParse(docInstIdProp.GetString(), out var docInstId):
            {
                var resolved = await ResolveDocumentInstanceAsync(docInstId, scope, refDepth, nodeDepth,
                    keepRefProvenance, ct);
                if (resolved.ValueKind == JsonValueKind.Undefined) return node.Clone();
                return keepRefProvenance
                    ? WithMeta(resolved, RefProvenance.InstanceIdKey, docInstId.ToString())
                    : resolved;
            }

            // Обрыв цикла (issue #330): instance-ссылка при allowInstanceRefs=false — один документ уже
            // развёрнут, глубже не рекурсируем (цикл A↔B). Отдаём ЧИСТЫЙ стаб (без $ref, но с
            // displayName/instanceId): сырой ссылки не остаётся → сканер не флагает её как «не найдена»,
            // повторный проход (двойной ResolveContextRefsAsync) не разворачивает её лишний раз, а шаблон
            // может показать имя. Не-найденная instance-ссылка (target удалён) сюда НЕ попадает — она в
            // ветке выше уходит в node.Clone() (остаётся $ref → корректно флагается).
            case "instance":
                return StripRef(node);

            // Неизвестный $ref (напр. не найденная catalog/document-ссылка) — оставляем как есть (клон).
            default:
                return node.Clone();
        }
    }

    /// <summary>Копия объекта без ключа «$ref» — стаб оборванной-по-циклу ссылки (issue #330):
    /// перестаёт быть ссылкой (не резолвится/не флагается), но сохраняет displayName/instanceId.</summary>
    private static JsonElement StripRef(JsonElement node)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in node.EnumerateObject())
            if (p.Name != "$ref")
                dict[p.Name] = p.Value.Clone();
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>Помечает разрешённую ССЫЛКУ сырым <see cref="TypeStamper.TypeIdKey"/> = ФАКТИЧЕСКИЙ тип
    /// записи (issue #344). Chain строит уже Application-<see cref="TypeStamper"/> (резолвер знает лишь
    /// собственный тип объекта — агностичность к схеме полей цела). Только для инлайна ссылок (не для
    /// профилей уровней, которые зовут тот же загрузчик, иначе сырой маркер утёк бы в data.json).</summary>
    private static JsonElement WithTypeId(JsonElement obj, Guid typeId)
        => WithMeta(obj, TypeStamper.TypeIdKey, typeId.ToString());

    /// <summary>Добавляет служебный ключ к развёрнутому объекту, заменяя одноимённый, если он есть.</summary>
    private static JsonElement WithMeta(JsonElement obj, string key, string value)
    {
        if (obj.ValueKind != JsonValueKind.Object) return obj;
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in obj.EnumerateObject())
            if (p.Name != key) dict[p.Name] = p.Value.Clone();
        dict[key] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Загружает DocumentInstance и разворачивает его поля той же единой обработкой, но с
    /// <c>allowInstanceRefs: false</c> — вложенные instance-ссылки дальше не разворачиваются
    /// (защита от циклов), а массивы/таблицы внутри обрабатываются как везде (это чинит баг B).
    /// </summary>
    private async Task<JsonElement> ResolveDocumentInstanceAsync(Guid instanceId, ScopeChain scope, int refDepth,
        int nodeDepth, bool keepRefProvenance, CancellationToken ct)
    {
        var obj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstOrDefaultAsync(o => o.Id == instanceId && o.ScopeLevel == CatalogScope.Set
                                      && o.ScopeId == scope.SetId && o.Facet != null, ct);

        if (obj is null) return default;

        var subCtx = GenerationContext.FromJson(obj.Data, obj.Facet!.PluginData);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in subCtx.Data)
            if (v is JsonElement je)
                dict[k] = await ResolveNode(je, scope, refDepth + 1, nodeDepth + 1, allowInstanceRefs: false,
                    keepRefProvenance, ct);

        // Фактический тип развёрнутого документа-ссылки (issue #344) — Application развернёт в _type.
        dict[TypeStamper.TypeIdKey] = JsonSerializer.SerializeToElement(obj.CompositeTypeId.ToString());
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Рекурсивно разрешает объект общих данных по id с поддержкой _baseRef: если в data есть
    /// "_baseRef", подтягивает данные базового объекта и выполняет deep-merge (собственные поля
    /// имеют приоритет). Цель может быть того же типа — прокси/роль (issue #89). Guard: база обязана
    /// быть в скоуп-поддереве документа (<paramref name="scope"/>) — вверх по оси/System да, вбок
    /// (другая стройка/раздел/комплект) нет; иначе утечка чужих данных. Цикл — <paramref name="visited"/>.
    /// </summary>
    private async Task<JsonElement> ResolveEntryByIdAsync(Guid id, ScopeChain scope, HashSet<Guid> visited, CancellationToken ct)
    {
        if (!visited.Add(id))
            return default; // защита от циклических ссылок

        var obj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (obj is null) return default;

        var ownData = obj.Data.RootElement;
        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl) || ParseBaseRef(baseRefEl) is not { } baseId)
            return ownData;

        // Scope-guard на цель базы/прокси (issue #89): вне скоуп-поддерева документа — не наследуем.
        var baseObj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == baseId, ct);
        if (baseObj is null || !scope.Contains(baseObj.ScopeLevel, baseObj.ScopeId))
            return ownData;

        var baseData = await ResolveEntryByIdAsync(baseId, scope, visited, ct);
        return baseData.ValueKind == JsonValueKind.Object ? MergeBaseObjects(baseData, ownData) : ownData;
    }

    /// <summary>
    /// Наследование от базового объекта (issue #71/#84): данные объекта могут нести "_baseRef" на
    /// ДРУГОЙ объект — документ комплекта ЛИБО запись общих данных (после слияния — единый DomainObject).
    /// Полиморфизм схлопнут: разновидность определяется природой цели (наличие фасеты), а не тегом ссылки.
    /// Guard: цель-документ — тот же комплект (same-set); цель-общие-данные — скоп-поддерево документа
    /// (иначе утечка чужих данных). Собственные поля переопределяют унаследованные (<see cref="MergeBaseObjects"/>).
    /// </summary>
    private async Task<JsonElement> ResolveObjectBaseRefAsync(
        JsonElement ownData, ScopeChain scope, HashSet<Guid> visited, CancellationToken ct)
    {
        if (ownData.ValueKind != JsonValueKind.Object) return ownData;
        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl)) return ownData;
        if (ParseBaseRef(baseRefEl) is not { } id) return ownData;

        var baseObj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (baseObj is null) return ownData;

        JsonElement baseData;
        if (baseObj.IsDocument)
        {
            if (!visited.Add(id)) return ownData; // цикл/самоссылка → без наследования
            if (baseObj.ScopeLevel != CatalogScope.Set || baseObj.ScopeId != scope.SetId) return ownData; // same-set guard
            baseData = await ResolveObjectBaseRefAsync(baseObj.Data.RootElement, scope, visited, ct);
        }
        else // запись общих данных
        {
            if (!scope.Contains(baseObj.ScopeLevel, baseObj.ScopeId)) return ownData; // scope-subtree guard
            baseData = await ResolveEntryByIdAsync(id, scope, visited, ct); // entry→entry цепочка (guard на всю цепь) + свой visited.Add(id)
        }

        return baseData.ValueKind == JsonValueKind.Object ? MergeBaseObjects(baseData, ownData) : ownData;
    }

    /// Разбор "_baseRef": делегирует общему <see cref="BaseRefReader.ParseRef"/> — единое правило
    /// для резолвера и guard'ов удаления (issue #71). Разновидность цели определяется её природой
    /// при резолве (наличие фасеты), поэтому здесь нужен только id — тег kind игнорируется.
    private static Guid? ParseBaseRef(JsonElement el) => BaseRefReader.ParseRef(el);

    /// <summary>
    /// Слияние двух JSON-объектов для _baseRef-наследования: базовые поля первыми, собственные
    /// переопределяют их на верхнем уровне; ключ "_baseRef" исключается. Чистая функция —
    /// общая для наследования любых объектов (issue #71/#84).
    /// </summary>
    private static JsonElement MergeBaseObjects(JsonElement baseData, JsonElement ownData)
    {
        // Делегируем общему BaseRefReader.MergeObjects — единое правило для резолва и flatten копии (#283).
        return BaseRefReader.MergeObjects(baseData, ownData);
    }
}
