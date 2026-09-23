using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class DocumentTypeHandlers(
    IRepository<DocumentType> repo,
    IRepository<DomainObject> objectRepo,
    IRepository<Template> templateRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IRepository<DocumentSetPlanItem> planRepo,
    IDataSetService dataSetService,
    IActivityLog journal,
    TagCatalog tags) :
    IRequestHandler<CreateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeSchemaCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeOwnerCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAbstractCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAllowsProxyCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeGroupCommand, DocumentType>,
    IRequestHandler<DeleteDocumentTypeCommand>,
    IRequestHandler<ListDocumentTypesQuery, IReadOnlyList<DocumentType>>,
    IRequestHandler<GetDocumentTypeQuery, DocumentType?>,
    IRequestHandler<GetDocumentTypeUsageQuery, DocumentTypeUsage>,
    IRequestHandler<AuditDocumentTypeQuery, DocumentTypeAuditReport>,
    IRequestHandler<AuditInstanceQuery, IReadOnlyList<AuditFinding>>,
    IRequestHandler<MigrateFieldKeyCommand, MigrateFieldKeyResult>,
    IRequestHandler<ApplyAuditFixesCommand, ApplyAuditFixesResult>
{
    /// <summary>
    /// Перенос ключа поля при переименовании его в схеме (issue #357) — и в данных, и у держателей
    /// ключа вне данных (issue #737).
    ///
    /// <para>Держателей три: реквизиты инстансов, привязки наборов данных (целевое поле и ключи
    /// маппинга) и шаблоны привязок типа. Перенеси мы только данные — привязка осталась бы на
    /// старом ключе и перестала заполнять поле, причём молча: человек переименовал поле и увидел
    /// пустоту там, где были данные. Ровно так и разъехался живой реестр исполнительной
    /// документации, с которого началась issue.</para>
    /// </summary>
    public async Task<MigrateFieldKeyResult> Handle(MigrateFieldKeyCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.OldKey) || string.IsNullOrWhiteSpace(cmd.NewKey) || cmd.OldKey == cmd.NewKey)
            return new MigrateFieldKeyResult(0, 0, 0);
        var all = await repo.GetAllAsync(ct);
        var byId = all.ToDictionary(t => t.Id);
        var typeIds = all.Where(t => Schema.DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, cmd.TypeId, byId))
            .Select(t => t.Id).ToList();
        var instances = (await objectRepo.FindAsync(o => typeIds.Contains(o.CompositeTypeId), ct)).ToList();

        var migrated = 0;
        foreach (var inst in instances)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(inst.Data.RootElement.GetRawText()) as System.Text.Json.Nodes.JsonObject;
            if (root is null) continue;
            if (Schema.JsonPathEditor.Rename(root, cmd.OldKey, cmd.NewKey, out _, out _))
            {
                inst.SetData(System.Text.Json.JsonDocument.Parse(root.ToJsonString()));
                objectRepo.Update(inst);
                migrated++;
            }
        }
        // Реквизиты — одним сохранением: либо все инстансы переехали, либо ни один.
        if (migrated > 0) await objectRepo.SaveChangesAsync(ct);

        // Держатели ключа вне реквизитов. Владельцы привязок — те же инстансы; шаблоны принадлежат
        // самому типу и его подтипам (ключ мог быть объявлен выше по цепочке).
        //
        // Второе сохранение, а не общая транзакция: обе половины идут через один контекст, но
        // делить транзакцию между слоями значило бы протащить её в контракт службы наборов.
        // Расхождение между половинами не тупик — операция ИДЕМПОТЕНТНА: повторный вызов не тронет
        // уже переехавшие реквизиты (старого ключа в них больше нет) и доделает привязки. А самое
        // вероятное исключение здесь — неразбираемый маппинг — обезврежено в RenameMappingKey.
        var holders = await dataSetService.MigrateFieldKeyAsync(
            instances.Select(i => i.Id).ToList(), typeIds, cmd.OldKey, cmd.NewKey, ct);

        return new MigrateFieldKeyResult(migrated, holders.Bindings, holders.Templates);
    }

    public async Task<IReadOnlyList<AuditFinding>> Handle(AuditInstanceQuery q, CancellationToken ct)
    {
        var inst = await objectRepo.GetByIdAsync(q.InstanceId, ct)
            ?? throw new NotFoundException($"Instance {q.InstanceId} not found");
        var byId = (await repo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var primitives = (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);

        // Ключ поля держат не только реквизиты (issue #737): привязки наборов данных ссылаются на
        // него своим TargetFieldKey и ключами маппинга. Осиротевший ключ ТАМ аудит данных не видит —
        // он сверяет Data, а разошлась настройка, и живой случай выглядел как «данные из ниоткуда».
        var bindings = await dataSetService.ListBindingsAsync(inst.Id, ct);
        var issues = Schema.SchemaDataAuditor.Audit(inst.Data.RootElement, inst.CompositeTypeId, byId, primitives)
            .Concat(Schema.BindingKeyAuditor.AuditBindings(bindings, inst.CompositeTypeId, byId));

        return issues
            .Select(iss => new AuditFinding(inst.Id, inst.DisplayName, iss.Code, iss.Severity.ToString(), iss.Path, iss.Message))
            .ToList();
    }

    public async Task<ApplyAuditFixesResult> Handle(ApplyAuditFixesCommand cmd, CancellationToken ct)
    {
        var outcomes = new List<AuditFixOutcome>();
        var touched = false;
        // Справочники схемы нужны только приведению (issue #643) — оно должно знать, К ЧЕМУ приводить.
        // Читаем лениво: пакет из одних удалений (обычный случай) не должен платить за два SELECT'а.
        Dictionary<Guid, DocumentType>? typesById = null;
        Dictionary<Guid, PrimitiveType>? primitivesById = null;
        async Task LoadSchemaAsync()
        {
            typesById ??= (await repo.GetAllAsync(ct)).ToDictionary(t => t.Id);
            primitivesById ??= (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        }
        // Группируем по инстансу — одну загрузку/мутацию Data на инстанс. Осиротевшие пути — ключи
        // объектов (не индексы массива), поэтому порядок применения внутри инстанса не сдвигает пути.
        foreach (var grp in cmd.Fixes.GroupBy(f => f.InstanceId))
        {
            var inst = await objectRepo.GetByIdAsync(grp.Key, ct);
            if (inst is null)
            {
                foreach (var f in grp) outcomes.Add(new(f.InstanceId, f.Path, f.Action, false, "Инстанс не найден", null));
                continue;
            }
            var root = System.Text.Json.Nodes.JsonNode.Parse(inst.Data.RootElement.GetRawText()) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
            var changed = false;
            foreach (var f in grp)
            {
                bool ok; string? oldVal = null; string? reason = null;
                if (f.Action == "remove")
                {
                    ok = Schema.JsonPathEditor.Remove(root, f.Path, out oldVal);
                    if (!ok) reason = "Значение уже отсутствует.";
                }
                else if (f.Action == "rename" && !string.IsNullOrWhiteSpace(f.TargetKey))
                    ok = Schema.JsonPathEditor.Rename(root, f.Path, f.TargetKey!, out oldVal, out reason);
                else if (f.Action == "coerce")
                {
                    await LoadSchemaAsync();
                    ok = TryCoerceAt(root, f.Path, inst.CompositeTypeId, typesById!, primitivesById!, out oldVal, out reason);
                }
                else { ok = false; reason = "Неизвестное действие."; }
                outcomes.Add(new(f.InstanceId, f.Path, f.Action, ok, reason, oldVal));
                changed |= ok;
            }
            if (changed)
            {
                inst.SetData(System.Text.Json.JsonDocument.Parse(root.ToJsonString()));
                objectRepo.Update(inst);
                touched = true;
            }
        }
        if (touched) await objectRepo.SaveChangesAsync(ct); // атомарно: один SaveChanges на все мутации
        return new(outcomes.Count(o => o.Applied), outcomes.Count(o => !o.Applied), outcomes);
    }

    /// <summary>
    /// Приведение значения по пути к объявленному типу поля (issue #643). Поле находим по СХЕМЕ
    /// (путь ведёт и внутрь строк таблиц), значение правим в дереве данных.
    /// </summary>
    private static bool TryCoerceAt(
        System.Text.Json.Nodes.JsonNode root, string path, Guid rootTypeId,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        out string? oldValue, out string? reason)
    {
        oldValue = null;
        var field = Schema.SchemaPathResolver.FieldAt(path, rootTypeId, typesById);
        if (field is null) { reason = "Поле не найдено в текущей схеме."; return false; }

        var current = Schema.JsonPathEditor.ValueAt(root, path);
        if (!Schema.ValueCoercion.TryCoerce(field, current, primitivesById, out var coerced, out reason))
            return false;
        if (!Schema.JsonPathEditor.Replace(root, path, coerced, out oldValue))
        {
            reason = "Значение по этому пути уже отсутствует.";
            return false;
        }
        return true;
    }

    public async Task<DocumentTypeAuditReport> Handle(AuditDocumentTypeQuery q, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        var byId = all.ToDictionary(t => t.Id);
        var type = byId.GetValueOrDefault(q.TypeId)
            ?? throw new NotFoundException($"DocumentType {q.TypeId} not found");

        // Аудит по типу = все инстансы типа И его подтипов (каждый — против СВОЕЙ эффективной схемы).
        var typeIds = all.Where(t => Schema.DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, q.TypeId, byId))
            .Select(t => t.Id).ToList();
        var instances = (await objectRepo.FindAsync(o => typeIds.Contains(o.CompositeTypeId), ct)).ToList();
        // Примитивы читаем один раз на прогон, а не на объект: тот же расчёт, что у справочников
        // проверки выпуска (см. SchemaCatalog, issue #628).
        var primitives = (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);

        // Привязки всех инстансов — ОДНИМ запросом (issue #737): поштучно это было бы обращение на
        // документ, а у типа их бывает сотня.
        var bindingsByOwner = (await dataSetService.ListBindingsForOwnersAsync(
                instances.Select(i => i.Id).ToList(), ct))
            .GroupBy(b => b.OwnerId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DataSets.DataSetBindingDto>)g.ToList());

        var findings = new List<AuditFinding>();
        foreach (var inst in instances)
        {
            var issues = Schema.SchemaDataAuditor.Audit(inst.Data.RootElement, inst.CompositeTypeId, byId, primitives)
                .Concat(Schema.BindingKeyAuditor.AuditBindings(
                    bindingsByOwner.GetValueOrDefault(inst.Id, []), inst.CompositeTypeId, byId));
            foreach (var iss in issues)
                findings.Add(new(inst.Id, inst.DisplayName, iss.Code, iss.Severity.ToString(), iss.Path, iss.Message));
        }

        // Шаблоны привязок принадлежат ТИПУ, а не документу: их находки не привязаны к инстансу, и
        // место документа в строке занимает сам тип — иначе непонятно, где искать настройку.
        //
        // Обходим ВЕСЬ поддерево (typeIds), как и инстансы: шаблон на подтипе проверяется против
        // схемы своего типа. Спроси мы только корень — аудит родителя показывал бы «чисто» на
        // поддереве, где чисто не было.
        foreach (var tid in typeIds)
            foreach (var iss in Schema.BindingKeyAuditor.AuditTemplates(
                         await dataSetService.ListTemplatesAsync(tid, ct), tid, byId))
                findings.Add(new(tid, $"Шаблоны привязок типа «{byId[tid].Name}»",
                    iss.Code, iss.Severity.ToString(), iss.Path, iss.Message));

        return new(q.TypeId, type.Name, instances.Count, findings);
    }

    public async Task<DocumentType> Handle(CreateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        EnsureUnique(all, cmd.Name, cmd.Code, excludeId: null);
        EnsureParentAllowsDerived(cmd.ParentId, all);

        // Тип, заведённый ЧЕЛОВЕКОМ в редакторе, рождается ОБЩИМ (ТЗ CORE-18 называет умолчанием
        // «закрыто» — но это умолчание для типа, который объявил модуль). Заводят такой тип затем,
        // чтобы его объекты попали в общие данные, в печать и в наборы; роди мы его закрытым, он
        // отличался бы от всех уже существующих типов, и отличие это всплыло бы не сегодня, а в
        // день, когда признак начнёт действовать.
        var dt = DocumentType.Create(
            cmd.Name.Trim(), cmd.Code.Trim(), cmd.Kind, cmd.ParentId, cmd.Schema,
            cmd.Module, TypeVisibility.Shared, cmd.IsAbstract);
        // Ограничения тэгов — ПОСЛЕ построения типа (issue #258, #959): новый тип может сразу нести
        // и ограниченный тэг, и второе поле с одиночным, а кратность считается по цепочке
        // наследования — то есть по типу, а не по одной схеме.
        ValidateTagRestrictions(dt, all);
        EnsureOwnershipHolds(dt, [.. all, dt]);
        await repo.AddAsync(dt, ct);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    /// <summary>
    /// Передача типа другому владельцу (ТЗ CORE-30). Нужна потому, что владельца существующим
    /// типам расставила миграция по явному списку, а список составлялся по смыслу: справочник,
    /// заведённый человеком, мог оказаться не у того владельца, и чинить это правкой базы руками —
    /// не починка.
    /// </summary>
    public async Task<DocumentType> Handle(SetDocumentTypeOwnerCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        var all = await repo.GetAllAsync(ct);
        var was = dt.Module;

        dt.SetOwner(cmd.Module);
        EnsureOwnershipHolds(dt, all);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);

        await journal.RecordAsync(ActivityActions.TypeOwnerChanged,
            dt.Id.ToString(), dt.Name, before: was, after: dt.Module, ct: ct);
        return dt;
    }

    /// <summary>
    /// Производный тип от ЗАКРЫТОГО типа не заводится (ТЗ CORE-19.1, последняя строка таблицы).
    /// Наследник — способ обойти замок: он добавляет поля, исключает унаследованные и переопределяет
    /// их, то есть делает ровно то, что закрытый уровень запрещает, только этажом ниже.
    /// </summary>
    private static void EnsureParentAllowsDerived(Guid? parentId, IReadOnlyList<DocumentType> all)
    {
        if (parentId is not { } id) return;
        var parent = all.FirstOrDefault(t => t.Id == id);
        if (parent is null || parent.EditLevel != SchemaEditLevel.Closed) return;
        throw new ConflictException(
            $"От типа «{parent.Name}» нельзя произвести новый: его схему задаёт модуль, " +
            "а производный тип менял бы её в обход — добавлял бы поля и исключал унаследованные.");
    }

    /// <summary>
    /// Правило «опора ядра — ядро» (ТЗ CORE-30), <see cref="TypeOwnershipRules" />.
    ///
    /// ⚠️ Проверяются ОБА направления, и второе менее очевидно: тип нельзя не только поставить на
    /// чужую опору, но и отдать модулю, если на него опирается тип ядра. Проверь мы одно
    /// направление — правило обходилось бы с другого конца, причём тем же действием.
    ///
    /// Проверяется только окрестность правки, а не вся база: расхождения, которые уже лежат в
    /// базе (например, после восстановления чужой копии), не должны запрещать не связанные с ними
    /// правки — иначе единственным способом починки осталась бы правка базы руками.
    /// </summary>
    private static void EnsureOwnershipHolds(DocumentType edited, IReadOnlyList<DocumentType> all)
    {
        var byId = all.ToDictionary(t => t.Id);
        var problems = new List<string>(TypeOwnershipRules.Violations(edited, byId));


        // У зависимых смотрим ТОЛЬКО их опору на этот тип: чужие расхождения, уже лежащие в базе,
        // не должны запрещать правку, к которой они не относятся.
        foreach (var dependent in all.Where(t => t.Id != edited.Id
                     && TypeOwnershipRules.SupportsOf(t).Any(s => s.TypeId == edited.Id)))
            problems.AddRange(TypeOwnershipRules.Violations(dependent, byId, onlySupport: edited.Id));

        if (problems.Count == 0) return;
        throw new ConflictException(
            "Опора типа ядра обязана принадлежать ядру: " + string.Join("; ", problems.Distinct()) +
            ". Иначе при выключенном модуле тип ядра остался бы без родителя или без вложенного " +
            "типа — то есть неописуемым, хотя сам никуда не делся.");
    }

    public async Task<DocumentType> Handle(UpdateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        var all = await repo.GetAllAsync(ct);
        EnsureUnique(all, cmd.Name, cmd.Code, excludeId: cmd.Id);
        // Prevent cycles: parentId must not be a descendant of this type
        if (cmd.ParentId.HasValue && IsDescendant(cmd.ParentId.Value, cmd.Id, all))
            throw new ConflictException("Нельзя установить дочерний тип в качестве родителя — возникнет цикл.");

        if (cmd.ParentId != dt.ParentId) EnsureParentAllowsDerived(cmd.ParentId, all);

        dt.Rename(cmd.Name.Trim(), cmd.Code.Trim());
        dt.SetParent(cmd.ParentId);
        // Смена родителя — ВТОРАЯ дверь к тому же нарушению кратности (issue #959, ревью PR #1012):
        // схема не менялась, но набор унаследованных полей стал другим, и одиночный тэг мог
        // оказаться сразу у двух полей. Проверка после SetParent: считать надо по НОВОЙ цепочке.
        ValidateTagRestrictions(dt, all);
        EnsureOwnershipHolds(dt, all);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // Код и имя типа документа должны быть уникальны (без учёта регистра и краёв).
    private static void EnsureUnique(IReadOnlyList<DocumentType> all, string name, string code, Guid? excludeId)
    {
        static string N(string s) => s.Trim().ToLowerInvariant();
        var nName = N(name);
        var nCode = N(code);
        foreach (var t in all)
        {
            if (excludeId.HasValue && t.Id == excludeId.Value) continue;
            if (N(t.Code) == nCode)
                throw new InvalidRequestException($"Тип документа с кодом «{code.Trim()}» уже существует.");
            if (N(t.Name) == nName)
                throw new InvalidRequestException($"Тип документа с именем «{name.Trim()}» уже существует.");
        }
    }

    private static bool IsDescendant(Guid candidateId, Guid ancestorId, IReadOnlyList<DocumentType> all)
    {
        var visited = new HashSet<Guid>();
        var current = candidateId;
        while (true)
        {
            if (current == ancestorId) return true;
            if (!visited.Add(current)) return false;
            var parent = all.FirstOrDefault(x => x.Id == current)?.ParentId;
            if (parent is null) return false;
            current = parent.Value;
        }
    }

    public async Task<DocumentType> Handle(UpdateDocumentTypeSchemaCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        // Ограничения тэгов (issue #258, #959): носителей считаем среди прочих типов + входящей
        // схемы, кратность — по типу С ВХОДЯЩЕЙ схемой (`WithSchema` даёт копию, не сущность БД:
        // подмена схемы у отслеживаемой записала бы черновик чужим SaveChanges в том же запросе).
        var all = await repo.GetAllAsync(ct);
        ValidateTagRestrictions(dt.WithSchema(cmd.Schema), all);

        // Уровень правки (ТЗ CORE-19.1): что администратору можно сделать со схемой ЭТОГО типа.
        // Проверка идёт ДО записи и сравнивает старую схему с новой по полям модуля.
        if (SchemaEditPolicy.Refusals(dt.Schema, cmd.Schema, dt.EditLevel) is { Count: > 0 } refusals)
            throw new ConflictException(
                $"Схему типа «{dt.Name}» так править нельзя: " + string.Join("; ", refusals) + ".");

        // Прежнее состояние — ДО правки: после UpdateSchema сравнивать уже не с чем.
        var wasFields = SchemaChangeSummary.Describe(dt.Schema);
        var change = SchemaChangeSummary.Describe(dt.Schema, cmd.Schema);

        dt.UpdateSchema(cmd.Schema);
        EnsureOwnershipHolds(dt, all);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);

        // Правка схемы — в журнал действий (ТЗ CORE-28). Пишем ПОСЛЕ сохранения: запись о том, чего
        // не случилось, хуже отсутствия записи.
        //
        // ⚠️ Сегодня пишутся правки ЛЮБОГО типа, а ТЗ называет уровни «расширяемый» и «закрытый».
        // Уровней в коде ещё нет (они придут с владельцем-модулем у типа, STG-5 п.7), и более узкое
        // условие было бы написано наугад. Шире — не ошибка: журнал знает лишнее, а не упускает
        // нужное; сузить его, когда уровни появятся, — одна строка здесь.
        await journal.RecordAsync(ActivityActions.TypeSchemaChanged,
            dt.Id.ToString(), dt.Name, before: wasFields, after: change, ct: ct);

        return dt;
    }

    /// <summary>
    /// Ограничения тэгов сохраняемой схемы. Бросает ConflictException (409) со списком занятых мест.
    ///
    /// Проверок две, и они про РАЗНОЕ: глобальный максимум носителей во всей системе (issue #258 —
    /// «профиль уровня во всей системе один») и кратность внутри одного типа (ТЗ TYPE-21, issue
    /// #959 — «одно поле типа на тэг»). Обе нарушаются независимо, поэтому сообщаются вместе:
    /// починив одну и получив отказ по второй, администратор решил бы, что ничего не изменилось.
    /// </summary>
    /// <param name="saving">Сохраняемый тип, уже несущий входящую схему.</param>
    private void ValidateTagRestrictions(DocumentType saving, IReadOnlyList<DocumentType> all)
    {
        var messages = TagRestrictionValidator
            .Validate(tags, saving.Schema, saving.Id, saving.Name, all)
            .Select(v => v.Describe())
            .Concat(TagCardinalityValidator.Validate(tags, saving, all).Select(v => v.Describe()))
            .ToList();
        if (messages.Count > 0) throw new ConflictException(string.Join(" ", messages));
    }

    public async Task<DocumentType> Handle(SetDocumentTypeAbstractCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetAbstract(cmd.IsAbstract);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task<DocumentType> Handle(SetDocumentTypeAllowsProxyCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetAllowsProxy(cmd.AllowsProxy);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task<DocumentType> Handle(SetDocumentTypeGroupCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetGroup(cmd.Group);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // issue #57: удаление типа не проверяло использование. Проверки вынесены в ComputeUsageAsync —
    // общий источник для guard'а удаления И проактивного показа (issue #275), чтобы не разъехались.
    public async Task Handle(DeleteDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var all = await repo.GetAllAsync(ct);
        var usage = await ComputeUsageAsync(dt, all, ct);
        if (usage.InUse)
            throw new ConflictException(
                "Нельзя удалить тип — используется. " + string.Join("; ", usage.Reasons.Select(FormatReason)) + ".");

        repo.Remove(dt);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<DocumentTypeUsage> Handle(GetDocumentTypeUsageQuery q, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(q.Id, ct) ?? throw new NotFoundException();
        return await ComputeUsageAsync(dt, await repo.GetAllAsync(ct), ct);
    }

    private static string FormatReason(DocumentTypeUsageReason r) =>
        r.Names.Count > 0 ? $"{r.Label}: {string.Join(", ", r.Names)}"
        : r.Count > 0 ? $"{r.Label}: {r.Count}"
        : r.Label;

    // Все причины, из-за которых тип нельзя удалить (issue #57 + #258 + #269). После слияния (issue #84)
    // документы и записи общих данных — единый DomainObject.CompositeTypeId, поэтому проверка объектов одна.
    private async Task<DocumentTypeUsage> ComputeUsageAsync(DocumentType dt, IReadOnlyList<DocumentType> all, CancellationToken ct)
    {
        var reasons = new List<DocumentTypeUsageReason>();

        var children = all.Where(x => x.ParentId == dt.Id).ToList();
        if (children.Count > 0)
            reasons.Add(new("children", "Наследуются типы", children.Count, children.Select(c => c.Name).ToList()));

        // issue #258: тип-профиль уровня (несёт тэг profile-*) — снять тэг перед удалением.
        if (SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileConstruction)
            || SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileSection)
            || SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileSet))
            reasons.Add(new("profile", "Назначен профилем уровня (снимите тэг «Профиль …»)", 0, []));

        var objects = await objectRepo.FindAsync(o => o.CompositeTypeId == dt.Id, ct);
        if (objects.Count > 0)
            reasons.Add(new("objects", "Созданы объекты (документы или записи общих данных)", objects.Count, []));

        // План по документам ссылается на тип (issue #796). Внешнего ключа там нет намеренно:
        // каскад унёс бы строки плана вместе с типом, и процент готовности молча поехал бы. Значит
        // единственная защита — здесь.
        var planned = await planRepo.FindAsync(p => p.DocumentTypeId == dt.Id, ct);
        if (planned.Count > 0)
            reasons.Add(new("plan", "Входит в план комплектов", planned.Count, []));

        var templates = await templateRepo.FindAsync(t => t.DocumentTypeId == dt.Id, ct);
        if (templates.Count > 0)
            reasons.Add(new("templates", "Шаблоны", templates.Count, []));

        var qdocs = await qualityDocRepo.FindAsync(qd => qd.DocumentTypeId == dt.Id, ct);
        if (qdocs.Count > 0)
            reasons.Add(new("quality", "Документы качества", qdocs.Count, []));

        var bindingTemplates = await dataSetService.ListTemplatesAsync(dt.Id, ct);
        if (bindingTemplates.Count > 0)
            reasons.Add(new("binding-templates", "Шаблоны привязки наборов данных", bindingTemplates.Count, []));

        if (await dataSetService.AnySourceMaterializedAsTypeAsync(dt.Id, ct))
            reasons.Add(new("materialized", "Материализован источник набора данных", 0, []));

        // Тип может использоваться как составной подтип в схеме ДРУГОГО типа (complex/array/doc-ref/
        // doc-array поле с typeId == dt.Id) — сам себя (собственную схему) не проверяем.
        var usedInSchemas = all.Where(t => t.Id != dt.Id && DocumentTypeSchemaReader.ReferencesType(t.Schema, dt.Id)).ToList();
        if (usedInSchemas.Count > 0)
            reasons.Add(new("subtype", "Используется как составной подтип в схеме", usedInSchemas.Count, usedInSchemas.Select(t => t.Name).ToList()));

        return new DocumentTypeUsage(reasons);
    }

    public async Task<IReadOnlyList<DocumentType>> Handle(ListDocumentTypesQuery q, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return q.Kind is null ? all : all.Where(x => x.Kind == q.Kind).ToList();
    }

    public Task<DocumentType?> Handle(GetDocumentTypeQuery q, CancellationToken ct)
        => repo.GetByIdAsync(q.Id, ct);
}
