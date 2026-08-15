using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Generation;

public class DataSetResolver(
    AppDbContext db,
    IDataSetRowLoader rowLoader,
    IObjectResolver objectResolver,
    ILogger<DataSetResolver> logger
) : IDataSetResolver
{
    /// <summary>Генерация документа: резолвит привязки владельца в контекст (scope — из комплекта документа).</summary>
    public Task InjectAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
        => ResolveBindingsCoreAsync(ctx, instance.Id, instance.DocumentTypeId,
            CatalogScope.Set, instance.DocumentSetId, diagnostics, ct);

    /// <summary>
    /// Резолв привязок для ПЕРСИСТА (issue #99): sync-on-save общих данных. Прогоняет тот же резолв-путь,
    /// что и генерация (@@ref → {$ref:catalog, entryId}, нет матча → пропуск + WARNING), но scope берётся
    /// из расположения объекта (ScopeLevel, ScopeId), а результат отдаётся значениями для слияния в Data.
    /// Ключевое отличие от превью: здесь резолвится ЗНАЧЕНИЕ (ссылка), а не display-строка «🔗 …».
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> ResolveOwnerBindingsAsync(
        Guid ownerId, Guid typeId, CatalogScope scopeLevel, Guid? scopeId,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
    {
        var ctx = new GenerationContext();
        await ResolveBindingsCoreAsync(ctx, ownerId, typeId, scopeLevel, scopeId, diagnostics, ct);
        return ctx.Data;
    }

    private async Task ResolveBindingsCoreAsync(GenerationContext ctx, Guid ownerId, Guid typeId,
        CatalogScope scopeLevel, Guid? scopeId, List<ResolutionDiagnostic>? diagnostics, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (bindings.Count == 0) return;

        // Схема типов (для кардинальности целевого поля материализации/табличной связки) — лениво, один раз.
        Dictionary<Guid, DocumentType>? typesById = null;
        async Task<Dictionary<Guid, DocumentType>> TypesAsync() =>
            typesById ??= await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        // Примитивы нужны, чтобы понять базовый тип поля при приведении значения (#466) — тоже лениво.
        Dictionary<Guid, PrimitiveType>? primitivesById = null;
        async Task<Dictionary<Guid, PrimitiveType>> PrimitivesAsync() =>
            primitivesById ??= await db.PrimitiveTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        // Поля типа по ключу — для приведения значения к объявленному типу.
        async Task<Dictionary<string, SchemaFieldInfo>> FieldsOfAsync(Guid? id) => id is { } tid
            ? DocumentTypeSchemaReader.EffectiveFields(tid, await TypesAsync())
                .GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First())
            : [];

        // Ячейка, которая должна была стать ссылкой на документ, но идентификатором не оказалась
        // (issue #715). Сказать об этом больше НЕКОМУ: ValueTypeScanner поля-ссылки пропускает
        // намеренно («их проверяет резолвер ссылок»), а ResolutionScanner ищет оставшиеся $ref —
        // здесь же остаётся обычная строка, и она молча уезжает в шаблон вместо документа. Ровно
        // так выглядит выбор соседней колонки в диалоге материализации: «Наименование» вместо «Ид».
        //
        // Предупреждением, а не ошибкой: тот же уровень, что у @@ref, не нашедшего запись каталога.
        // Данные накоплены, и строка в одной ячейке не повод не выпускать документ целиком.
        void WarnUnbuiltDocRef(SchemaFieldInfo? field, object? value, string token, string path)
        {
            // Токен-конструктор (@@ref/@@inline/@@file) сюда не относится: он строит своё значение и
            // о своих неудачах отчитывается сам.
            if (field?.Type != "doc-ref" || value is not string raw || token.StartsWith("@@", StringComparison.Ordinal))
                return;

            var shown = raw.Length > 60 ? raw[..60] + "…" : raw;
            diagnostics?.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning, path,
                $"Ссылка на документ не построена: в колонке «{token}» не идентификатор, а «{shown}»."));
        }

        foreach (var binding in bindings)
        {
            try
            {
                // Целевое поле привязки исчезло из схемы (issue #737). Отказываем ДО загрузки набора:
                // качать файл ради записи в мёртвый ключ незачем.
                //
                // Молча писать «не туда» хуже честного отказа. Живой случай: поле переименовали
                // «ОсновнойДокументы» → «ОсновныеДокументы», человек завёл привязку заново, а старая
                // осталась — и продолжала наливать устаревшие данные в ключ, которого в схеме нет.
                // В data.json они попадали, шаблон их не ждал, аудит инстанса о них не знал (он
                // сверяет реквизиты, а тут разошлась привязка), и найти это можно было только
                // глазами в отладочном ZIP.
                //
                // Ключи вне схемы в контексте легальны у вычисляемых полей и служебных «_», но у
                // привязки легального случая нет: интерфейс заводит её только по полю схемы.
                //
                // Предупреждение, а не ошибка, и это осознанно: Error обрывает выпуск документа
                // целиком (GenerateDocumentHandler бросает ResolutionValidationException на любой),
                // и живой комплект с одной устаревшей привязкой перестал бы и генерироваться, и
                // показываться в предпросмотре. Данные при этом всё равно не пишутся — цель
                // достигнута, — а несобираемый документ был бы лечением тяжелее болезни. Тот же
                // довод, что у соседнего WarnUnbuiltDocRef.
                if (binding.TargetFieldKey is { } targetKey
                    && DocumentTypeSchemaReader.Field(typeId, targetKey, await TypesAsync()) is null)
                {
                    diagnostics?.Add(new ResolutionDiagnostic(
                        DiagnosticSeverity.Warning, targetKey,
                        $"Привязка источника «{binding.Source.Name}» указывает на несуществующее поле " +
                        $"«{targetKey}» — поле не заполнено, данные в документ не попадают. " +
                        "Удалите привязку или переключите её на поле текущей схемы."));
                    continue;
                }

                // Download → parse → transformation → filter → sort (shared with preview via DataSetRowLoader).
                var rows = await rowLoader.LoadRowsAsync(binding.Source, ct);

                // Материализация ссылкой на существующий документ (issue #725). Проверяем ДО маппинга:
                // в этом режиме маппинга нет вовсе, и общая ветка отказала бы «маппинг колонок пуст» —
                // то есть пожаловалась бы на настройку, которой в этом режиме и не должно быть.
                //
                // Действует только когда материализация вообще применяется (собственный маппинг
                // привязки замещает её целиком — то же правило, что у дискриминатора #716).
                if (binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping)
                    && MaterializeByIdMode.IsOn(binding.Source.MaterializeByIdColumn))
                {
                    InjectDocumentRefs(ctx, binding, typeId, rows, await TypesAsync(), diagnostics);
                    continue;
                }

                // Материализация на источнике (issue #19): если источник настроен на материализацию, а
                // привязка не несёт собственного маппинга — маппинг берётся с источника (тип↔тип), а
                // привязка играет роль типизированного указателя. Иначе — легаси-маппинг привязки.
                var mappingJson = DataSetMappingValue.EffectiveMappingJson(
                    binding.Mapping, binding.Source.MaterializeTypeId, binding.Source.MaterializeMapping);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];

                // Материализация настроена, а маппинг пуст — говорим об этом вслух (issue #715).
                //
                // Молчать здесь нельзя: резолвер честно прогоняет каждую строку через пустой маппинг
                // и складывает в поле массив пустых объектов. Снаружи это неотличимо от «источник
                // отдал пустые строки», и живой кейс (union из doc-ref-вариантов, маппить которые
                // грамматика не умела вовсе) выглядел именно так — настройка есть, результата нет,
                // и ни одного слова о причине.
                //
                // Проверяем только материализованный источник. Пустой маппинг у обычной привязки —
                // это «ещё не настроено», и видно это в самой привязке; а здесь пользователь ВЫБРАЛ
                // тип материализации, то есть настройку сделал, и она не работает.
                //
                // «Пусто» здесь ровно то же, что и у EffectiveMappingJson двумя строками выше —
                // IsEmptyMapping считает пустым и маппинг из одних пустых значений. Проверка по
                // Count пропускала бы {"Документ":""}: ключ есть, строить нечего, массив пустышек тот же.
                if (binding.Source.MaterializeTypeId is not null && DataSetMappingValue.IsEmptyMapping(mappingJson))
                {
                    diagnostics?.Add(new ResolutionDiagnostic(
                        DiagnosticSeverity.Error,
                        binding.TargetFieldKey ?? "(скалярная привязка)",
                        $"Источник «{binding.Source.Name}» материализован, но маппинг колонок пуст — " +
                        "поле не заполнено. Задайте маппинг в диалоге материализации источника."));
                    continue;
                }

                // Вариант union'а по типу документа строки (issue #716). Действует только когда
                // маппинг взят С ИСТОЧНИКА: собственный маппинг привязки замещает материализацию
                // целиком, вместе с правилом — плоский маппинг построчной вариативности не выражает,
                // и оставить правило в силе значило бы применять его к чужим ключам.
                var byMaterialization = binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping);
                var selector = byMaterialization
                    ? await BuildVariantSelectorAsync(
                        binding.Source.MaterializeDiscriminator, rows, await TypesAsync(), ct)
                    : null;
                // Причины пропуска копим и говорим ОДНОЙ строкой: реестр на сотню документов дал бы
                // сотню одинаковых предупреждений, за которыми не видно ничего.
                var skipped = new Dictionary<string, int>(StringComparer.Ordinal);

                if (binding.TargetFieldKey is null)
                {
                    // Скалярный: первая строка → отдельные поля контекста
                    if (rows.Count > 0)
                    {
                        var row = rows[0];
                        var ownFields = await FieldsOfAsync(typeId);
                        var primitives = await PrimitivesAsync();
                        // При дискриминаторе даже здесь заполняется РОВНО ОДИН вариант — тот, что
                        // выбран по первой строке. Иначе union перестал бы быть union'ом.
                        var scalarPairs = PairsFor(selector, mapping, row, skipped);
                        foreach (var (fieldKey, mapVal) in scalarPairs)
                        {
                            var field = ownFields.GetValueOrDefault(fieldKey);
                            // Та же проверка, что у табличной привязки (issue #737), только ключей здесь
                            // несколько: в скалярном режиме целевые поля перечисляет маппинг. Переживи
                            // маппинг переименование поля — часть ключей осиротеет поодиночке, и без
                            // отказа документ получил бы половину значений молча, а не «пусто целиком».
                            if (field is null)
                            {
                                // Предупреждение по той же причине, что и у табличной привязки выше:
                                // Error здесь снял бы с выпуска весь документ, причём из-за одного
                                // ключа маппинга — остальные поля этой же привязки заполняются.
                                diagnostics?.Add(new ResolutionDiagnostic(
                                    DiagnosticSeverity.Warning, fieldKey,
                                    $"Маппинг привязки источника «{binding.Source.Name}» указывает на " +
                                    $"несуществующее поле «{fieldKey}» — значение не записано."));
                                continue;
                            }
                            var value = await ApplyMappedAsync(mapVal, row, ownerId, scopeLevel, scopeId, diagnostics, fieldKey, ct);
                            value = DataSetValueCoercion.Coerce(value, field, primitives, await TypesAsync());
                            WarnUnbuiltDocRef(field, value, mapVal, fieldKey);
                            if (value is not null)
                                // Собранный объект (ссылка на документ, @@ref, @@inline) кладём
                                // JsonElement'ом. Иначе он невидим обоим страховочным проходам:
                                // и ResolveContextRefsAsync, и ScanLeftoverRefs пропускают всё, что
                                // не JsonElement, — то есть сырой маркер уехал бы в data.json и в
                                // сохранённые данные записи неразрешённым и никем не замеченным.
                                ctx.Set(fieldKey, value is Dictionary<string, object?> composite
                                    ? JsonSerializer.SerializeToElement(composite)
                                    : value);
                        }
                    }
                }
                else
                {
                    // Кардинальность решает ТИП целевого поля: complex/doc-ref ← первая сущность;
                    // array/doc-array (и всё прочее) ← весь поток. Вычисляем ДО построения строк —
                    // нужен и для кардинальности, и для defaultValue (issue #53, часть 2).
                    var field = DocumentTypeSchemaReader.Field(typeId, binding.TargetFieldKey, await TypesAsync());

                    // defaultValue незамапленных полей ТИПА СТРОКИ (issue #53, часть 2): для табличных
                    // биндингов маппинг покрывает только явно перечисленные ключи (свои — binding.Mapping,
                    // либо MaterializeMapping источника) — поля целевого типа, не попавшие в маппинг (но
                    // имеющие defaultValue схемы, напр. через fieldOverrides унаследованного поля), иначе
                    // никогда не появляются в результате. Тип строки — MaterializeTypeId источника, если
                    // маппинг взят оттуда (см. EffectiveMappingJson), иначе — типId самого целевого поля.
                    var rowTypeId = byMaterialization ? binding.Source.MaterializeTypeId : field?.TypeId;
                    var rowDefaults = rowTypeId is { } rtid && selector is null
                        ? DocumentTypeSchemaReader.EffectiveFields(rtid, await TypesAsync())
                            .Where(f => f.DefaultValue is not null && SchemaFieldKinds.IsScalar(f.Type))
                            .ToList()
                        // При дискриминаторе умолчаний не подставляем: строка union'а обязана нести
                        // ровно один ключ, а умолчание чужого варианта добавило бы второй.
                        : [];

                    // Все строки → объекты формы целевого типа. Храним как JsonElement, чтобы повторный
                    // проход EntityResolver разрешил добавленные ссылки $ref на каталог.
                    var mapped = new List<Dictionary<string, object?>>();
                    var rowIndex = 0;
                    var rowFields = await FieldsOfAsync(rowTypeId);
                    var rowPrimitives = await PrimitivesAsync();
                    var rowTypes = await TypesAsync();
                    foreach (var row in rows)
                    {
                        var pairs = PairsFor(selector, mapping, row, skipped);
                        // Строка, которой не досталось варианта, в результат НЕ попадает: пустой
                        // объект среди реестра выглядел бы строкой без данных, а её там нет вовсе.
                        if (selector is not null && pairs.Count == 0) continue;

                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, mapVal) in pairs)
                        {
                            var path = $"{binding.TargetFieldKey}[{rowIndex}].{fieldKey}";
                            var rowField = rowFields.GetValueOrDefault(fieldKey);
                            var value = await ApplyMappedAsync(mapVal, row, ownerId, scopeLevel, scopeId, diagnostics, path, ct);
                            // Число в числовом поле, а не текст ячейки (#466).
                            value = DataSetValueCoercion.Coerce(value, rowField, rowPrimitives, rowTypes);
                            WarnUnbuiltDocRef(rowField, value, mapVal, path);
                            if (value is not null)
                                obj[fieldKey] = value;
                        }
                        // Приоритет ниже маппинга: замапленное поле умолчание НЕ подменяет — даже когда
                        // ячейка пуста. Проверять наличие ключа для этого нельзя (issue #544): пустая
                        // числовая ячейка ключа больше не создаёт, и намеренно оставленный пробел в
                        // документе стал бы значением из схемы — для протокола измерений это означало бы
                        // напечатанный ноль там, где измерения просто не было.
                        foreach (var f in rowDefaults)
                            if (!mapping.ContainsKey(f.Key) && !obj.ContainsKey(f.Key))
                                obj[f.Key] = f.DefaultValue!.Value;
                        mapped.Add(obj);
                        rowIndex++;
                    }

                    if (field is not null && DocumentTypeSchemaReader.IsSingleComposite(field.Type))
                    {
                        if (mapped.Count > 0)
                            ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(mapped[0]));
                    }
                    else
                    {
                        ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(mapped));
                    }
                }

                ReportSkipped(binding.TargetFieldKey ?? "(скалярная привязка)", skipped, diagnostics);
            }
            catch (Exception ex)
            {
                // Пропускаем невалидные привязки, чтобы не блокировать генерацию,
                // но фиксируем причину — иначе "пустые" поля невозможно отладить.
                logger.LogWarning(ex,
                    "Привязка набора данных пропущена. BindingId={BindingId}, SourceId={SourceId}, Owner={OwnerId}",
                    binding.Id, binding.SourceId, ownerId);
                // Иначе поле просто исчезает без следа — поднимаем причину в диагностику.
                diagnostics?.Add(new ResolutionDiagnostic(
                    DiagnosticSeverity.Error,
                    binding.TargetFieldKey ?? "(скалярная привязка)",
                    $"Источник данных недоступен — поле не заполнено. {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Материализация «существующий документ по Ид» (issue #725): каждая строка источника кладётся в
    /// целевое поле ссылкой на документ, живые данные подставит второй проход <c>EntityResolver</c>.
    ///
    /// Строки без идентификатора (пустая ячейка, не-Ид) в результат НЕ попадают: ссылка без Ид — это
    /// битая ссылка, которую сканер потом объявит удалённой записью, то есть выдуманной бедой.
    /// Молчания при этом нет — причины уходят в ту же сводку, что и пропуски дискриминатора.
    ///
    /// Скалярная привязка (без целевого поля) в этом режиме бессмысленна: раскладывать ссылку по
    /// отдельным полям контекста нечем — у неё нет полей. Отказываемся словами, а не тишиной.
    /// </summary>
    private static void InjectDocumentRefs(
        GenerationContext ctx, DataSetBinding binding, Guid ownerTypeId,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        Dictionary<Guid, DocumentType> typesById,
        List<ResolutionDiagnostic>? diagnostics)
    {
        if (binding.TargetFieldKey is null)
        {
            diagnostics?.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Error, "(скалярная привязка)",
                $"Источник «{binding.Source.Name}» материализован ссылкой на существующий документ — " +
                "такую строку можно положить только в поле-документ или в список документов, " +
                "а не в отдельные поля."));
            return;
        }

        var column = binding.Source.MaterializeByIdColumn!;
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var refs = new List<Dictionary<string, object?>>();

        foreach (var row in rows)
        {
            var (id, reason, _) = MaterializeByIdMode.ReadId(row, column);
            if (id is null)
            {
                skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
                continue;
            }
            refs.Add(MaterializeByIdMode.RefValue(id.Value));
        }

        var field = DocumentTypeSchemaReader.Field(ownerTypeId, binding.TargetFieldKey, typesById);
        if (field is not null && DocumentTypeSchemaReader.IsSingleComposite(field.Type))
        {
            if (refs.Count > 0)
                ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(refs[0]));
        }
        else
        {
            ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(refs));
        }

        ReportSkipped(binding.TargetFieldKey, skipped, diagnostics);
    }

    /// <summary>
    /// Готовит выбор варианта union'а по строке (issue #716); null — дискриминатора нет, материализация
    /// статична (ровно один вариант на все строки, прежнее поведение).
    ///
    /// Типы документов по идентификаторам разрешаются ОДНИМ запросом на всю страницу строк: реестр на
    /// сотню документов иначе дал бы сотню запросов, а кэшировать это между вызовами нельзя — состав
    /// комплекта меняется, и вчерашний ответ соврал бы.
    /// </summary>
    private async Task<MaterializeVariantSelector?> BuildVariantSelectorAsync(
        string? discriminatorJson,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        CancellationToken ct)
    {
        var config = MaterializeVariantSelector.ParseConfig(discriminatorJson);
        if (config is null) return null;

        Dictionary<Guid, Guid>? typeByDocument = null;
        var documentIds = MaterializeVariantSelector.DocumentIdsIn(config, rows).ToList();
        if (documentIds.Count > 0)
            typeByDocument = await db.DomainObjects.AsNoTracking()
                .Where(o => documentIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.CompositeTypeId, ct);

        return MaterializeVariantSelector.Create(config, typesById, typeByDocument);
    }

    /// <summary>
    /// Что применять к этой строке: без дискриминатора — весь маппинг, с ним — единственную пару
    /// победившего варианта. Пустой список означает «строка пропущена», причина уже посчитана.
    /// </summary>
    private static List<KeyValuePair<string, string>> PairsFor(
        MaterializeVariantSelector? selector,
        Dictionary<string, string> mapping,
        IReadOnlyDictionary<string, string?> row,
        Dictionary<string, int> skipped)
    {
        if (selector is null) return [.. mapping];

        var choice = selector.Choose(row);
        if (choice.VariantKey is null)
        {
            if (choice.SkipReason is { } reason)
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
            return [];
        }

        if (!mapping.TryGetValue(choice.VariantKey, out var token) || string.IsNullOrEmpty(token))
        {
            // Валидатор такого не пропускает, но настройка могла быть сохранена до его появления —
            // и тогда честнее сказать, чем построить объект без единого значения.
            skipped[MaterializeSkipReason.VariantNotMapped] =
                skipped.GetValueOrDefault(MaterializeSkipReason.VariantNotMapped) + 1;
            return [];
        }

        return [new KeyValuePair<string, string>(choice.VariantKey, token)];
    }

    /// <summary>
    /// Пропущенные строки — ОДНОЙ строкой со сводкой по причинам. Реестр на сотню документов дал бы
    /// сотню одинаковых предупреждений, за которыми не разглядеть ни одного.
    ///
    /// Ничья вариантов — ошибка, а не предупреждение: это противоречие настройки (один тип назначен
    /// двум вариантам одинаково точно), и чинится оно правкой, а не данными.
    /// </summary>
    private static void ReportSkipped(
        string path, Dictionary<string, int> skipped, List<ResolutionDiagnostic>? diagnostics)
    {
        if (diagnostics is null || skipped.Count == 0) return;

        var total = skipped.Values.Sum();
        var detail = string.Join("; ", skipped
            .OrderByDescending(p => p.Value)
            .Select(p => $"{p.Value} — {MaterializeSkipReason.Describe(p.Key)}"));
        var severity = skipped.ContainsKey(MaterializeSkipReason.Ambiguous)
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;

        diagnostics.Add(new ResolutionDiagnostic(severity, path,
            $"Строк пропущено при материализации: {total} ({detail})."));
    }

    /// <summary>
    /// Применяет одно значение маппинга через общий <see cref="DataSetMappingApplier"/> (issue #374):
    /// колонка/@@file/@@inline — общие ветки, @@ref делегируется <see cref="ResolveRefAsync"/> (резолв в
    /// существующую запись каталога). Inline строит встроенный объект; его @@ref-под-поля дают $ref,
    /// доразрешаемый 2-м проходом резолвера.
    /// </summary>
    private Task<object?> ApplyMappedAsync(
        string mapVal,
        IReadOnlyDictionary<string, string?> row,
        Guid ownerId,
        CatalogScope scopeLevel,
        Guid? scopeId,
        List<ResolutionDiagnostic>? diagnostics,
        string path,
        CancellationToken ct)
        => DataSetMappingApplier.ApplyAsync(mapVal, row,
            (rm, r, p, c) => ResolveRefAsync(rm, r, ownerId, scopeLevel, scopeId, diagnostics, p, c), path, ct);

    /// <summary>
    /// @@ref: резолвит строку в существующий объект каталога через единый <see cref="IObjectResolver"/>
    /// (issue #183) — по имени/алиасам или составному identity-ключу. Нет матча → WARNING + null (создание
    /// объектов не выполняется, резолвер read-only). Возвращает {$ref:catalog, entryId} по найденной записи.
    /// </summary>
    private async Task<object?> ResolveRefAsync(
        DataSetRefMapping refMap,
        IReadOnlyDictionary<string, string?> row,
        Guid ownerId,
        CatalogScope scopeLevel,
        Guid? scopeId,
        List<ResolutionDiagnostic>? diagnostics,
        string path,
        CancellationToken ct)
    {
        // Ссылочное поле: резолвим строку в существующий объект каталога по одной из двух стратегий
        // (issue #243): Identity (составной ключ identity-полей) либо Name (по имени/алиасам); legacy
        // с непустым match — Field (произвольное поле, из UI больше не создаётся, читается вечно).
        ObjectMatchRequest req;
        string lookupDisplay; // для WARNING
        if (refMap.IsIdentity)
        {
            var fields = new Dictionary<string, string?>();
            foreach (var (idField, col) in refMap.IdentityColumns!)
                fields[idField] = row.TryGetValue(col, out var cv) ? cv : null;
            if (fields.Values.All(string.IsNullOrWhiteSpace)) return null; // нечего искать
            req = ObjectMatchRequest.ByIdentity(refMap.TypeId, fields);
            lookupDisplay = string.Join(" · ", fields.Values.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        else
        {
            if (refMap.Column is null || !row.TryGetValue(refMap.Column, out var lookup) || string.IsNullOrWhiteSpace(lookup))
                return null;
            req = string.IsNullOrEmpty(refMap.Match)
                ? ObjectMatchRequest.ByName(refMap.TypeId, lookup)
                : ObjectMatchRequest.ByField(refMap.TypeId, refMap.Match, lookup);
            lookupDisplay = lookup;
        }

        var entryId = await objectResolver.ResolveAsync(req, scopeLevel, scopeId, ct);
        if (entryId is null)
        {
            logger.LogWarning(
                "Запись каталога не найдена при маппинге набора данных. TypeId={TypeId}, Strategy={Strategy}, Value={Value}, Owner={OwnerId}",
                refMap.TypeId, req.Strategy, lookupDisplay, ownerId);
            diagnostics?.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning, path,
                $"Значение «{lookupDisplay}» не найдено в каталоге — ссылка не подставлена."));
            return null;
        }

        return new Dictionary<string, object?> { ["$ref"] = "catalog", ["entryId"] = entryId.Value.ToString() };
    }

}
