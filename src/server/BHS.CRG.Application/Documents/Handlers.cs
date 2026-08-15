using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
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
    IDataSetService dataSetService) :
    IRequestHandler<CreateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeSchemaCommand, DocumentType>,
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
        // Ограничения тэгов (issue #258): новый тип может сразу нести restricted-тэг (POST несёт схему).
        ValidateTagRestrictions(cmd.Schema, Guid.Empty, cmd.Name.Trim(), all);

        var dt = DocumentType.Create(cmd.Name.Trim(), cmd.Code.Trim(), cmd.Kind, cmd.ParentId, cmd.Schema, cmd.IsAbstract);
        await repo.AddAsync(dt, ct);
        await repo.SaveChangesAsync(ct);
        return dt;
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

        dt.Rename(cmd.Name.Trim(), cmd.Code.Trim());
        dt.SetParent(cmd.ParentId);
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
        // Ограничения тэгов (issue #258): считаем носителей среди прочих типов + входящей схемы.
        var all = await repo.GetAllAsync(ct);
        ValidateTagRestrictions(cmd.Schema, dt.Id, dt.Name, all);
        dt.UpdateSchema(cmd.Schema);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // Бросает ConflictException (маппится в 409) со списком занятых мест — issue #258.
    private static void ValidateTagRestrictions(JsonDocument schema, Guid savingId, string savingName,
        IReadOnlyList<DocumentType> all)
    {
        var violations = TagRestrictionValidator.Validate(schema, savingId, savingName, all);
        if (violations.Count > 0)
            throw new ConflictException(string.Join(" ", violations.Select(v => v.Describe())));
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

public class ConstructionHandlers(
    IRepository<Construction> constructionRepo,
    IRepository<Section> sectionRepo,
    IScopeCascade cascade) :
    IRequestHandler<CreateConstructionCommand, Construction>,
    IRequestHandler<RenameConstructionCommand, Construction>,
    IRequestHandler<DeleteConstructionCommand>,
    IRequestHandler<GetConstructionQuery, Construction?>,
    IRequestHandler<ListConstructionsQuery, IReadOnlyList<Construction>>,
    IRequestHandler<GetSectionQuery, Section?>,
    IRequestHandler<CreateSectionCommand, Section>,
    IRequestHandler<RenameSectionCommand, Section>,
    IRequestHandler<DeleteSectionCommand>
{
    public async Task<Construction> Handle(CreateConstructionCommand cmd, CancellationToken ct)
    {
        var c = Construction.Create(cmd.Name, cmd.UserId);
        await constructionRepo.AddAsync(c, ct);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Construction> Handle(RenameConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        c.Rename(cmd.Name);
        constructionRepo.Update(c);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Удаление стройки. Разделы и комплекты уносит каскад базы, объекты на полиморфной оси — нет:
    /// их удаляем прикладно, иначе документы и общие данные всего поддерева остаются сиротами
    /// (issue #739). Guard тот же, что у поштучного удаления: держатели ссылок ИЗВНЕ поддерева.
    /// </summary>
    public async Task Handle(DeleteConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var plan = await cascade.PlanAsync(CatalogScope.Construction, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "стройку");
        cascade.Remove(plan);
        constructionRepo.Remove(c);
        await constructionRepo.SaveChangesAsync(ct);
    }

    public Task<Construction?> Handle(GetConstructionQuery q, CancellationToken ct)
        => constructionRepo.GetByIdAsync(q.Id, ct);

    public Task<IReadOnlyList<Construction>> Handle(ListConstructionsQuery q, CancellationToken ct)
        => constructionRepo.GetAllAsync(ct);

    public Task<Section?> Handle(GetSectionQuery q, CancellationToken ct)
        => sectionRepo.GetByIdAsync(q.Id, ct);

    public async Task<Section> Handle(CreateSectionCommand cmd, CancellationToken ct)
    {
        _ = await constructionRepo.GetByIdAsync(cmd.ConstructionId, ct)
            ?? throw new NotFoundException("Construction not found");
        var section = Section.Create(cmd.ConstructionId, cmd.Name);
        await sectionRepo.AddAsync(section, ct);
        await sectionRepo.SaveChangesAsync(ct);
        return section;
    }

    public async Task<Section> Handle(RenameSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        s.Rename(cmd.Name);
        sectionRepo.Update(s);
        await sectionRepo.SaveChangesAsync(ct);
        return s;
    }

    /// <inheritdoc cref="Handle(DeleteConstructionCommand, CancellationToken)" />
    public async Task Handle(DeleteSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var plan = await cascade.PlanAsync(CatalogScope.Section, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "раздел");
        cascade.Remove(plan);
        sectionRepo.Remove(s);
        await sectionRepo.SaveChangesAsync(ct);
    }
}

public class DocumentSetHandlers(
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IDomainObjectRepository objRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IReferenceIndex refIndex,
    IBlobStorage blobStorage,
    IScopeSubtree scopeSubtree,
    IScopeCascade cascade) :
    IRequestHandler<CreateDocumentSetCommand, DocumentSet>,
    IRequestHandler<RenameDocumentSetCommand, DocumentSet>,
    IRequestHandler<DeleteDocumentSetCommand>,
    IRequestHandler<GetDocumentSetQuery, DocumentSet?>,
    IRequestHandler<ListAvailableInstancesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<AddDocumentToSetCommand, DomainObject>,
    IRequestHandler<ReorderDocumentInstancesCommand, DocumentSet>,
    IRequestHandler<RenameDocumentInstanceCommand, DomainObject>,
    IRequestHandler<DeleteDocumentInstanceCommand>,
    IRequestHandler<DuplicateDocumentInstanceCommand, DomainObject>,
    IRequestHandler<CopyDocumentToSetCommand, CopyResult>,
    IRequestHandler<PreviewCopyDocumentQuery, IReadOnlyList<CopyWarning>>,
    IRequestHandler<MoveDocumentToSetCommand, CopyResult>,
    IRequestHandler<PreviewMoveDocumentQuery, MovePreview>,
    IRequestHandler<UpdateRequisitesCommand, DomainObject>,
    IRequestHandler<UpdatePluginDataCommand, DomainObject>,
    IRequestHandler<GetDocumentInstanceQuery, DomainObject?>,
    IRequestHandler<SetDocumentTemplateCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplatesCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplateParamsCommand, DomainObject>
{
    public async Task<DocumentSet> Handle(CreateDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = DocumentSet.Create(cmd.SectionId, cmd.Name);
        await setRepo.AddAsync(set, ct);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task<DocumentSet> Handle(RenameDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        set.Rename(cmd.Name);
        setRepo.Update(set);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task Handle(DeleteDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // Всё, что висит на оси (Set, этот Id) — документы, Set-скоуп общих данных, документы
        // качества уровня комплекта и связки материалов, — принадлежит комплекту: FK-каскада на
        // комплект нет (единая ось, полиморфный ScopeId), удаляем прикладно.
        // issue #739: и тот же guard, что у поштучного удаления, — иначе каскад обходит его с фланга.
        var plan = await cascade.PlanAsync(CatalogScope.Set, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "комплект");
        cascade.Remove(plan);
        setRepo.Remove(set);
        await setRepo.SaveChangesAsync(ct);
    }

    public Task<DocumentSet?> Handle(GetDocumentSetQuery q, CancellationToken ct)
        => setRepo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<DomainObject>> Handle(ListAvailableInstancesQuery q, CancellationToken ct)
    {
        // Доступны документы всей стройки: сначала поднимаемся от комплекта к ней, потом спускаемся
        // обратно ко всем её комплектам. Спуск — общий (issue #625), своей копии здесь больше нет.
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException();
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new NotFoundException();

        var setIds = await scopeSubtree.SetIdsUnderAsync(CatalogScope.Construction, section.ConstructionId, ct);
        return await objRepo.GetDocumentsInSetsAsync(setIds, ct);
    }

    public async Task<DomainObject> Handle(AddDocumentToSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.DocumentSetId, ct)
            ?? throw new NotFoundException();
        var docs = await objRepo.GetSetDocumentsAsync(cmd.DocumentSetId, tracked: false, ct);
        // Новый документ — в конец комплекта (порядок сборки задаётся SortOrder).
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        var obj = DomainObject.Create(cmd.DocumentTypeId, null, JsonDocument.Parse("{}"),
            CatalogScope.Set, cmd.DocumentSetId);
        obj.EnsureFacet();
        obj.SetSortOrder(maxOrder + 1);
        set.TouchUpdatedAt();
        setRepo.Update(set);
        await objRepo.AddAsync(obj, ct);
        await objRepo.SaveChangesAsync(ct);
        return obj;
    }

    public async Task<DocumentSet> Handle(ReorderDocumentInstancesCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.SetId, ct) ?? throw new NotFoundException();
        var docs = await objRepo.GetSetDocumentsAsync(cmd.SetId, tracked: true, ct);
        // Присваиваем SortOrder по позиции в переданном списке; отсутствующие в списке документы
        // (напр. добавленные параллельно) — в конец, сохраняя их относительный порядок.
        var order = cmd.OrderedInstanceIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var next = cmd.OrderedInstanceIds.Count;
        foreach (var d in docs.OrderBy(d => d.SortOrder))
            d.SetSortOrder(order.TryGetValue(d.Id, out var pos) ? pos : next++);
        await objRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task<DomainObject> Handle(RenameDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        obj.Rename(cmd.Name);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        return obj;
    }

    public async Task Handle(DeleteDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // issue #71/#269: удаление объекта, на который ссылаются (базовый экземпляр "_baseRef" или
        // "$ref" в значениях полей), оставило бы висячую ссылку — при генерации она молча
        // разворачивается в ничто (EntityResolver возвращает исходный узел / пропускает базу).
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя удалить документ — на него ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");
        objRepo.Remove(obj);
        await objRepo.SaveChangesAsync(ct);
    }

    // issue #283 (фаза B): дубль в ТОТ ЖЕ комплект. Ссылки/_baseRef валидны в том же scope —
    // сохраняем как есть (cross-set скраб — отдельные команды copy/move). Свежий черновик без PDF.
    public async Task<DomainObject> Handle(DuplicateDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        if (!source.IsDocument) throw new ConflictException("Дублировать можно только документ комплекта.");
        var setId = source.ScopeId!.Value;

        var docs = await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        // Deep-clone Data (независимый JsonDocument): _baseRef и $ref сохраняются — тот же комплект.
        var data = JsonDocument.Parse(source.Data.RootElement.GetRawText());
        var baseName = source.DisplayName
            ?? (await docTypeRepo.GetByIdAsync(source.CompositeTypeId, ct))?.Name
            ?? "документа";
        var clone = DomainObject.CloneAsDocument(source, setId, data, $"Копия {baseName}");
        clone.SetSortOrder(maxOrder + 1);

        await objRepo.AddAsync(clone, ct);
        await objRepo.SaveChangesAsync(ct);
        return clone;
    }

    // issue #283 (фаза C): копирование в ДРУГОЙ комплект. Оригинал остаётся (входящий guard не нужен —
    // referrer'ы всё ещё указывают на живой оригинал; guard только для move, фаза D).
    public async Task<CopyResult> Handle(CopyDocumentToSetCommand cmd, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(cmd.SourceId, cmd.TargetSetId, ct);
        var (data, warnings) = await BuildCopyPlanAsync(source, targetSet, cmd.Strategy, ct);

        var docs = await objRepo.GetSetDocumentsAsync(targetSet.Id, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);
        var baseName = source.DisplayName ?? (await docTypeRepo.GetByIdAsync(source.CompositeTypeId, ct))?.Name ?? "документа";
        var clone = DomainObject.CloneAsDocument(source, targetSet.Id, data, baseName);
        clone.SetSortOrder(maxOrder + 1);

        targetSet.TouchUpdatedAt();
        setRepo.Update(targetSet);
        await objRepo.AddAsync(clone, ct);
        await objRepo.SaveChangesAsync(ct);
        return new CopyResult(clone, warnings);
    }

    public async Task<IReadOnlyList<CopyWarning>> Handle(PreviewCopyDocumentQuery q, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(q.SourceId, q.TargetSetId, ct);
        var (_, warnings) = await BuildCopyPlanAsync(source, targetSet, q.Strategy, ct);
        return warnings;
    }

    // issue #283 (фаза D): перенос в другой комплект. Входящий guard (как удаление #269): если на
    // документ ссылаются — блокируем (в исходном комплекте ссылка повиснет). Тот же скраб исходящих,
    // что и copy; PDF сбрасываются (контекст резолва сменился).
    public async Task<CopyResult> Handle(MoveDocumentToSetCommand cmd, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(cmd.SourceId, cmd.TargetSetId, ct);
        var srcSetId = source.ScopeId!.Value;
        if (srcSetId == targetSet.Id) throw new ConflictException("Документ уже в этом комплекте.");

        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, source.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя перенести документ — на него ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");

        var (data, warnings) = await BuildCopyPlanAsync(source, targetSet, cmd.Strategy, ct);
        var docs = await objRepo.GetSetDocumentsAsync(targetSet.Id, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        var blobs = source.ResetToDraft(); // собранный вывод обоих комплектов устаревает
        source.SetData(data);
        source.MoveToSet(targetSet.Id);
        source.SetSortOrder(maxOrder + 1);

        targetSet.TouchUpdatedAt();
        setRepo.Update(targetSet);
        if (await setRepo.GetByIdAsync(srcSetId, ct) is { } srcSet) { srcSet.TouchUpdatedAt(); setRepo.Update(srcSet); }
        objRepo.Update(source);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return new CopyResult(source, warnings);
    }

    public async Task<MovePreview> Handle(PreviewMoveDocumentQuery q, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(q.SourceId, q.TargetSetId, ct);
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, source.Id, ct);
        var (_, warnings) = await BuildCopyPlanAsync(source, targetSet, q.Strategy, ct);
        return new MovePreview(warnings, referrers.Select(r => r.Label).ToList());
    }

    private async Task<(DomainObject source, DocumentSet targetSet)> LoadCopyEndpointsAsync(Guid sourceId, Guid targetSetId, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(sourceId, ct) ?? throw new NotFoundException();
        if (!source.IsDocument) throw new ConflictException("Копировать можно только документ комплекта.");
        var targetSet = await setRepo.GetByIdAsync(targetSetId, ct) ?? throw new NotFoundException("Целевой комплект не найден.");
        return (source, targetSet);
    }

    /// Скраб исходящих ссылок (стратегия B) + сбор предупреждений. Data результата — независимый JsonDocument.
    private async Task<(JsonDocument Data, IReadOnlyList<CopyWarning> Warnings)> BuildCopyPlanAsync(
        DomainObject source, DocumentSet targetSet, CopyStrategy strategy, CancellationToken ct)
    {
        _ = strategy; // сейчас только SmartCleanup; Snapshot — фаза C2.
        var warnings = new List<CopyWarning>();

        // 1) flatten _baseRef — запекаем унаследованные значения (иначе same-set guard молча потеряет их).
        var (flattened, didFlatten) = await FlattenBaseAsync(source.Data.RootElement, new HashSet<Guid>(), ct);
        if (didFlatten)
            warnings.Add(new CopyWarning("baseref", "Базовый экземпляр запечён в значения", 1, []));

        var section = await sectionRepo.GetByIdAsync(targetSet.SectionId, ct);

        // 2) стрип $ref:document/instance — same-set, в чужом комплекте = мусор. Кроме ссылок на
        //    документы качества, видимые из ЦЕЛЕВОГО комплекта (issue #733): у instance-ссылки два
        //    домена, и второй живёт по цепочке областей, а не по комплекту, — стерев его, мы
        //    выбросили бы рабочие данные и назвали бы их «ссылками на документы комплекта».
        var keepIds = await VisibleQualityRefsAsync(flattened, targetSet, section?.ConstructionId, ct);
        var (scrubbed, strippedFields) = RefScrubber.StripInstanceRefs(flattened, keepIds);
        if (strippedFields.Count > 0)
            warnings.Add(new CopyWarning("doc-ref", "Удалены ссылки на документы комплекта", strippedFields.Count, strippedFields));

        // 3) $ref:catalog — оставляем, но проверяем разрешимость в scope целевого комплекта.
        var unresolved = 0;
        foreach (var catId in RefReader.CollectRefIds(scrubbed).Distinct())
        {
            var obj = await objRepo.GetByIdAsync(catId, ct);
            if (obj is null || !InTargetSubtree(obj, targetSet, section?.ConstructionId)) unresolved++;
        }
        if (unresolved > 0)
            warnings.Add(new CopyWarning("catalog-unresolved", "Ссылки на каталог не разрешатся в новом расположении", unresolved, []));

        return (JsonDocument.Parse(scrubbed.GetRawText()), warnings);
    }

    // Рекурсивный flatten базового экземпляра: base-first merge, drop _baseRef; cycle-guard через visited.
    private async Task<(JsonElement Data, bool Flattened)> FlattenBaseAsync(JsonElement data, HashSet<Guid> visited, CancellationToken ct)
    {
        if (BaseRefReader.GetBaseRefId(data) is not { } baseId || !visited.Add(baseId)) return (data, false);
        var baseObj = await objRepo.GetByIdAsync(baseId, ct);
        if (baseObj is null) return (data, false); // висячая база — нечего запекать
        var (baseData, _) = await FlattenBaseAsync(baseObj.Data.RootElement, visited, ct);
        return (BaseRefReader.MergeObjects(baseData, data), true);
    }

    private static bool InTargetSubtree(DomainObject o, DocumentSet targetSet, Guid? targetConstructionId) => o.ScopeLevel switch
    {
        CatalogScope.System => true,
        CatalogScope.Construction => o.ScopeId == targetConstructionId,
        CatalogScope.Section => o.ScopeId == targetSet.SectionId,
        CatalogScope.Set => o.ScopeId == targetSet.Id,
        _ => false,
    };

    /// <summary>
    /// Идентификаторы документов качества, на которые ссылается <paramref name="data"/> и которые
    /// ОСТАНУТСЯ видимыми из целевого комплекта (issue #733) — их ссылки скраб не трогает.
    ///
    /// <para>Видимость считается тем же правилом, что у остальной библиотеки и у резолвера: System
    /// всегда, иначе совпадение по стройке/разделу/комплекту цели переноса. Сертификат уровня System
    /// переживает перенос куда угодно, сертификат чужой стройки — стирается вместе с остальными
    /// неразрешимыми ссылками, и это верно: в новом расположении он бы не развернулся.</para>
    /// </summary>
    private async Task<IReadOnlySet<Guid>> VisibleQualityRefsAsync(
        JsonElement data, DocumentSet targetSet, Guid? targetConstructionId, CancellationToken ct)
    {
        var ids = RefReader.CollectRefIds(data).Distinct().ToHashSet();
        if (ids.Count == 0) return new HashSet<Guid>();

        var docs = await qualityDocRepo.FindAsync(d => ids.Contains(d.Id), ct);
        return docs.Where(d => d.Scope switch
            {
                CatalogScope.System => true,
                CatalogScope.Construction => d.ScopeId == targetConstructionId,
                CatalogScope.Section => d.ScopeId == targetSet.SectionId,
                CatalogScope.Set => d.ScopeId == targetSet.Id,
                _ => false,
            })
            .Select(d => d.Id)
            .ToHashSet();
    }

    public async Task<DomainObject> Handle(UpdateRequisitesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft();
        obj.SetData(cmd.Requisites);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(UpdatePluginDataCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft();
        obj.UpdatePluginData(cmd.PluginData);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public Task<DomainObject?> Handle(GetDocumentInstanceQuery q, CancellationToken ct)
        => objRepo.GetByIdAsync(q.Id, ct);

    public async Task<DomainObject> Handle(SetDocumentTemplateCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft();
        obj.SetTemplate(cmd.TemplateId);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplatesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft(); // смена набора шаблонов меняет вывод — в черновик
        obj.SetTemplateIds(cmd.TemplateIds);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplateParamsCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft(); // параметры влияют на вывод — сбрасываем в черновик
        obj.SetTemplateParams(cmd.Params);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }
}

public class CommonDataHandlers(
    IRepository<DomainObject> repo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<Construction> constructionRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IReferenceIndex refIndex,
    IDataSetResolver dataSetResolver,
    ILevelProfileService levelProfiles) :
    IRequestHandler<CreateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<UpdateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<GetCommonDataEntryQuery, DomainObject?>,
    IRequestHandler<ResolveCommonDataForSetQuery, IReadOnlyList<CommonDataEntryWithScope>>,
    IRequestHandler<ResolveCommonDataForScopeQuery, IReadOnlyList<CommonDataEntryWithScope>>
{
    public async Task<DomainObject> Handle(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        // Запись общих данных — DomainObject БЕЗ документной фасеты (issue #84).
        var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<DomainObject> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // Резолв-путь (issue #99): @@ref → {$ref:catalog, entryId}, а не display-строка «🔗 …».
        // Scope — из расположения объекта. Нет матча → поле не пишется (резолвер пропускает).
        var resolved = await dataSetResolver.ResolveOwnerBindingsAsync(
            cmd.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId, null, ct);
        var data = resolved.Count == 0 ? cmd.Data : CommonDataBindingMerge.Merge(cmd.Data, resolved);
        entry.Update(cmd.DisplayName, data, cmd.Aliases);
        repo.Update(entry);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task Handle(DeleteCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // issue #258: объект-профиль (на который ссылается FK контейнера) — синглтон, удалять нельзя.
        if ((await constructionRepo.FindAsync(c => c.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await sectionRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await setRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0)
            throw new ConflictException("Это профиль уровня — его нельзя удалить. Он редактируется на странице «Общие данные» уровня.");
        // issue #71/#269: запись, на которую ссылаются другие объекты (базовый экземпляр "_baseRef"
        // или "$ref" в значениях полей), — тот же guard, что и для документа: иначе висячая ссылка.
        var referrers = await DomainObjectReferences.FindReferrersAsync(repo, qualityDocRepo, refIndex, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя удалить запись — на неё ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");
        repo.Remove(entry);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<DomainObject?> Handle(GetCommonDataEntryQuery q, CancellationToken ct)
        => await repo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<DomainObject>> Handle(ListCommonDataEntriesQuery q, CancellationToken ct)
    {
        var scope = q.Scope;
        var scopeId = q.ScopeId;
        var typeId = q.CompositeTypeId;
        // Ленивое создание профиля уровня (issue #258): при открытии общих данных контейнерного уровня
        // гарантируем объект-профиль (если профиль-тип сконфигурирован) — он попадёт в список ниже.
        if (scope is { } s && s != CatalogScope.System && scopeId is { } sid)
            await levelProfiles.EnsureProfileAsync(s, sid, ct);
        // Только общие данные (без документной фасеты).
        return await repo.FindAsync(e => e.Facet == null &&
            (!scope.HasValue || e.ScopeLevel == scope.Value) &&
            (!scopeId.HasValue || e.ScopeId == scopeId.Value) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);
    }

    public async Task<IReadOnlyList<CommonDataEntryWithScope>> Handle(
        ResolveCommonDataForSetQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException("DocumentSet not found");
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new NotFoundException("Section not found");
        var constructionId = section.ConstructionId;
        var setId = q.SetId;
        var sectionId = set.SectionId;
        var typeId = q.CompositeTypeId;

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            ((e.ScopeLevel == CatalogScope.Set          && e.ScopeId == setId) ||
             (e.ScopeLevel == CatalogScope.Section       && e.ScopeId == sectionId) ||
             (e.ScopeLevel == CatalogScope.Construction  && e.ScopeId == constructionId) ||
             e.ScopeLevel == CatalogScope.System) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        return Project(relevant);
    }

    public async Task<IReadOnlyList<CommonDataEntryWithScope>> Handle(
        ResolveCommonDataForScopeQuery q, CancellationToken ct)
    {
        // Разрешаем родительскую цепочку скопа: Set→Section→Construction→System (issue #82).
        // Неразрешённые уровни — Guid.Empty: ни одна запись со ScopeId==Empty не совпадёт.
        Guid setId = Guid.Empty, sectionId = Guid.Empty, constructionId = Guid.Empty;
        switch (q.Scope)
        {
            case CatalogScope.Set when q.ScopeId is { } sid:
                setId = sid;
                var set = await setRepo.GetByIdAsync(sid, ct);
                if (set is not null)
                {
                    sectionId = set.SectionId;
                    var sec = await sectionRepo.GetByIdAsync(set.SectionId, ct);
                    if (sec is not null) constructionId = sec.ConstructionId;
                }
                break;
            case CatalogScope.Section when q.ScopeId is { } secId:
                sectionId = secId;
                var section = await sectionRepo.GetByIdAsync(secId, ct);
                if (section is not null) constructionId = section.ConstructionId;
                break;
            case CatalogScope.Construction when q.ScopeId is { } cid:
                constructionId = cid;
                break;
            // System — родителей нет.
        }
        var typeId = q.CompositeTypeId;

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            ((e.ScopeLevel == CatalogScope.Set          && e.ScopeId == setId) ||
             (e.ScopeLevel == CatalogScope.Section       && e.ScopeId == sectionId) ||
             (e.ScopeLevel == CatalogScope.Construction  && e.ScopeId == constructionId) ||
             e.ScopeLevel == CatalogScope.System) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        return Project(relevant);
    }

    private static List<CommonDataEntryWithScope> Project(IReadOnlyList<DomainObject> entries) =>
        entries
            .Select(e => new CommonDataEntryWithScope(
                e.Id, e.DisplayName ?? "", e.CompositeTypeId, e.Data,
                e.ScopeLevel, e.ScopeId, (int)e.ScopeLevel,
                e.CreatedAt, e.UpdatedAt))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.DisplayName)
            .ToList();
}
