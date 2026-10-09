using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Configuration;

public partial class ArchiveReadInventoryTests
{
    /// <summary>
    /// Места чтения и решение у каждого. Ключ — «файл|строка кода»; причина обязательна у всех:
    /// перепись читают, чтобы понять замысел, а не чтобы убедиться, что она непустая.
    /// </summary>
    private static readonly Dictionary<string, Row> Reads = new(StringComparer.Ordinal)
    {
        ["BHS.CRG.Api/Endpoints/Core/ConstructionEndpoints.cs|var counts = await objRepo.CountDocumentsInSetsAsync(setIds, ct);"] =
            Documents("счётчик документов комплекта").X(2),
        ["BHS.CRG.Api/Endpoints/Core/EmployeeEndpoints.cs|return Results.Ok((await m.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId)))"] =
            Shown("страница сотрудников — показ: архивная карточка на месте"),
        ["BHS.CRG.Api/Endpoints/Core/EmployeeEndpoints.cs|var entry = await m.Send(new GetCommonDataEntryQuery(id));"] =
            Shown("карточка сотрудника по идентификатору: редактор и действия над ней").X(3),
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|return Results.Ok((await m.Send(new ListCommonDataEntriesQuery(records, parsedScope, scopeId, typeId)))"] =
            ByPurpose("назначение называет клиент параметром purpose; без него — отказ", nameof(ArchiveReadPurposeTests.Адрес_списка_требует_назначение_и_отвечает_по_нему)),
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|return Results.Ok((await m.Send(new ResolveCommonDataForScopeQuery(parsed.Value, scopeId, records, typeId, archivedOnly)))"] =
            ByPurpose("назначение называет клиент параметром purpose; без него — отказ", nameof(ArchiveReadPurposeTests.Адрес_списка_требует_назначение_и_отвечает_по_нему)),
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|return Results.Ok((await m.Send(new ResolveCommonDataForSetQuery(setId, records, typeId))).Select(Elide));"] =
            ByPurpose("назначение называет клиент параметром purpose; без него — отказ", nameof(ArchiveReadPurposeTests.Адрес_списка_требует_назначение_и_отвечает_по_нему)),
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|var entry = await m.Send(new GetCommonDataEntryQuery(id));"] =
            Shown("запись по идентификатору — редактору: признак в ответе"),
        ["BHS.CRG.Api/Endpoints/Documents/DocumentSetEndpoints.cs|var docs = await objRepo.GetSetDocumentsAsync(id, tracked: false, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Api/Endpoints/Documents/DocumentSetEndpoints.cs|var docs = await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Api/Endpoints/Documents/PrintFormEndpoints.cs|var instance = await instanceRepo.GetByIdAsync(instanceId, ct);"] =
            Documents("документ по идентификатору"),
        ["BHS.CRG.Api/Modules/Ports/ModuleCatalogPort.cs|entries.AddRange(await mediator.Send(new ListCommonDataEntriesQuery(records, null, null, id), ct));"] =
            ByPurpose("назначение называет модуль обязательным параметром порта", nameof(ArchiveReadPurposeTests.Порт_модулей_список_по_назначению_поиск_всегда_выбор_остальное_с_признаком)),
        ["BHS.CRG.Api/Modules/Ports/ModuleCatalogPort.cs|return Refs(await mediator.Send(new CommonDataRefsByIdsQuery(codes.Keys, ids), ct), codes);"] =
            Shown("названия и запись по идентификаторам: то, что у модуля уже стоит"),
        ["BHS.CRG.Api/Modules/Ports/ModuleCatalogPort.cs|var entry = await mediator.Send(new GetCommonDataEntryQuery(id), ct);"] =
            Shown("названия и запись по идентификаторам: то, что у модуля уже стоит"),
        ["BHS.CRG.Api/Modules/Ports/ModuleCatalogPort.cs|var found = await mediator.Send(new SearchCommonDataForChoiceQuery(codes.Keys, query, limit), ct);"] =
            Choice("поиск порта — всегда выбор", nameof(ArchiveReadPurposeTests.Порт_модулей_список_по_назначению_поиск_всегда_выбор_остальное_с_признаком)),
        ["BHS.CRG.Api/Modules/Ports/ModuleOwnCatalogPort.cs|var entry = await objects.GetByIdAsync(id, ct);"] =
            Shown("своя запись модуля по идентификатору — переименование и удаление"),

        ["BHS.CRG.Application/Documents/CommonDataBindingCheck.cs|return targets[entryId] = Guid.TryParse(entryId, out var g) ? await repo.GetByIdAsync(g, ct) : null;"] =
            Shown("сверка связки: цель по идентификатору, архивная получает статус archived"),
        ["BHS.CRG.Application/Documents/CommonDataBindingCheck.cs|var entry = await repo.GetByIdAsync(q.Id, ct) ?? throw new NotFoundException();"] =
            Seen("запись, чьи связки проверяют, — по идентификатору"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var archived = await repo.GetByIdAsync(twin.Id, ct);"] =
            Seen("отказ «есть в архиве» при создании: название архивной записи для текста отказа"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|return await Db.Set<DomainObject>().AsNoTracking()"] =
            Shown("какие из стоящих в форме ссылок — в архиве: ответ и есть признак"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.FieldValues.cs|(Db.Set<DomainObject>().AsNoTracking().Where(o =>"] =
            Shown("значения поля у записей вида (сопоставление по ИНН, issue #1077): назначение называет звавший, выбор архивные скрывает, показ отдаёт с признаком"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.FieldValues.cs|(Db.Set<DomainObject>().AsNoTracking().Where(o => wanted.Contains(o.Id)))"] =
            Seen("основы наследников — по идентификатору из _baseRef: значение архивной основы у живой роли не пропадает"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|ownerId is { } id && await repo.GetByIdAsync(id, ct) is { } owner"] =
            Seen("вынос в общие данные: объект-источник читается ради стоявших в нём ссылок, архив ли он сам — не важно"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|=> await objects.RefsByIdsAsync(q.TypeIds, q.Ids, ct);"] =
            Shown("названия уже стоящих ссылок"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|=> await objects.SearchForChoiceAsync(q.TypeIds, q.Search, q.Limit, ct);"] =
            Choice("кандидаты на выбор", nameof(ArchiveReadPurposeTests.Поиск_на_выбор_архив_не_отдаёт_но_называет_числом_а_по_идентификаторам_находит)),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|=> await repo.GetByIdAsync(q.Id, ct);"] =
            Shown("одна запись по идентификатору: признак несёт она сама"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|return await repo.FindAsync(e => e.Facet == null &&"] =
            ByPurpose("список уровня: отбор по назначению запроса", nameof(ArchiveReadPurposeTests.Список_уровня_скрывает_архив_в_выборе_и_показывает_в_показе)),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();"] =
            Seen("правка записи по идентификатору: архивную править можно"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var entry = await repo.GetByIdAsync(id, ct) ?? throw new NotFoundException();"] =
            Seen("удаление записи по идентификатору, обычное и принудительное (issue #1187): архивную удалять можно"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var relevant = await repo.FindAsync(e => e.Facet == null &&"] =
            ByPurpose("списки комплекта и цепочки уровней: отбор по назначению запроса", nameof(ArchiveReadPurposeTests.Список_комплекта_скрывает_архив_в_выборе_и_показывает_в_показе), nameof(ArchiveReadPurposeTests.Список_цепочки_уровней_скрывает_архив_в_выборе_и_показывает_в_показе)).X(2),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|=> objRepo.GetByIdAsync(q.Id, ct);"] =
            Documents("документ по идентификатору"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|return await objRepo.GetDocumentsInSetsAsync(setIds, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var baseObj = await objRepo.GetByIdAsync(baseId, ct);"] =
            Seen("копирование запекает базовый экземпляр по сохранённой ссылке"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var docs = await objRepo.GetSetDocumentsAsync(cmd.DocumentSetId, tracked: false, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var docs = await objRepo.GetSetDocumentsAsync(cmd.SetId, tracked: true, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var docs = await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var docs = await objRepo.GetSetDocumentsAsync(targetSet.Id, tracked: false, ct);"] =
            Documents("документы комплекта").X(2),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var obj = await objRepo.GetByIdAsync(catId, ct);"] =
            Seen("перенос документа: разрешится ли СОХРАНЁННАЯ ссылка в новом месте; архивная цель на месте"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();"] =
            Documents("документ по идентификатору").X(2),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();"] =
            Documents("документ по идентификатору").X(5),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var source = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();"] =
            Documents("документ по идентификатору"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var source = await objRepo.GetByIdAsync(sourceId, ct) ?? throw new NotFoundException();"] =
            Documents("документ по идентификатору"),
        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|await using (var rows = await objectRepo.ReadForUpdateAsync(o => typeIds.Contains(o.CompositeTypeId), ct))"] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|await using var rows = await objectRepo.ReadForUpdateAsync("] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|await using var row = await objRepo.ReadForUpdateAsync([cmd.SourceId], ct);"] =
            Documents("документ комплекта под блокировкой строки — записей общих данных здесь нет (issue #1232)"),
        ["BHS.CRG.Application/Generation/GenerateDocumentHandler.cs|await using var row = await instanceRepo.ReadForUpdateAsync([instance.Id], ct);"] =
            Documents("документ комплекта под блокировкой строки — записей общих данных здесь нет (issue #1232)"),
        ["BHS.CRG.Api/Endpoints/Documents/PrintFormEndpoints.cs|await using var row = await objects.ReadForUpdateAsync([instance.Id], ct);"] =
            Documents("документ комплекта под блокировкой строки — записей общих данных здесь нет (issue #1232)"),
        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|var inst = await objectRepo.GetByIdAsync(q.InstanceId, ct)"] =
            Service("аудит типа и починка значений: архивные записи обязаны чиниться вместе с остальными"),
        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|var instances = (await objectRepo.FindAsync(o => typeIds.Contains(o.CompositeTypeId), ct)).ToList();"] =
            Service("аудит типа и починка значений: архивные записи обязаны чиниться вместе с остальными"),
        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|var objects = await objectRepo.FindAsync(o => o.CompositeTypeId == dt.Id, ct);"] =
            Seen("тип занят объектами и не удаляется: архивная запись держит тип так же, как живая"),
        ["BHS.CRG.Application/Documents/PlanHandlers.cs|var actual = await objects.CountReadyDocumentsByTypeAsync([q.SetId], ct);"] =
            Documents("готовые документы для плана"),
        ["BHS.CRG.Application/Documents/PlanHandlers.cs|var actual = await objects.CountReadyDocumentsByTypeAsync(setsWithPlan, ct);"] =
            Documents("готовые документы для плана"),
        ["BHS.CRG.Application/Documents/RecordArchiveCommands.cs|var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();"] =
            Service("само действие «в архив / вернуть»: читает название и тип, признак меняет служба"),
        ["BHS.CRG.Application/Documents/RecordArchiveCommands.cs|var entry = await repo.GetByIdAsync(q.Id, ct);"] =
            Service("подсказка «можно ли предложить архив» к отказу в удалении"),
        ["BHS.CRG.Application/Generation/GenerateDocumentHandler.cs|var instance = await instanceRepo.GetByIdAsync(cmd.InstanceId, ct)"] =
            Documents("документ для генерации, предпросмотра и проверки — по идентификатору или типу"),
        ["BHS.CRG.Application/Generation/GetGenerationDebugBundle.cs|var instance = await instanceRepo.GetByIdAsync(q.InstanceId, ct);"] =
            Documents("документ для генерации, предпросмотра и проверки — по идентификатору или типу"),
        ["BHS.CRG.Application/Generation/InstanceResolutionValidator.cs|var instance = await instanceRepo.GetByIdAsync(instanceId, ct)"] =
            Documents("документ для генерации, предпросмотра и проверки — по идентификатору или типу"),
        ["BHS.CRG.Application/Generation/MigrateToPrefixedAddressing.cs|repin.AddRange((await instanceRepo.GetDocumentsOfTypeAsync(typeId, ct))"] =
            Documents("документ для генерации, предпросмотра и проверки — по идентификатору или типу"),
        ["BHS.CRG.Application/Generation/PreviewDocument.cs|var instance = await instanceRepo.GetByIdAsync(q.InstanceId, ct);"] =
            Documents("документ для генерации, предпросмотра и проверки — по идентификатору или типу"),
        ["BHS.CRG.Application/Objects/BaseRefReader.cs|: await objRepo.FindAsync(o => objectIds.Contains(o.Id), ct);"] =
            Seen("цепочка базовых экземпляров: сохранённая ссылка, архивная база продолжает отдавать данные"),
        ["BHS.CRG.Application/Objects/ScopeCascade.cs|var objects = await objRepo.FindAsync(o => o.ScopeId != null"] =
            Service("каскад удаления уровня: уходит всё, что на нём лежит"),
        ["BHS.CRG.Application/QualityDocs/QualitySetAudit.cs|var documents = await objects.GetSetDocumentsAsync(setId, tracked: false, ct);"] =
            Documents("документы комплекта"),
        ["BHS.CRG.Application/Resolution/ResolveObjectsBatch.cs|: (await repo.FindAsync(o => matched.Contains(o.Id), ct))"] =
            Shown("имена совпавших объектов после резолвера: признак архива едет в ответе, подставлять ли — решает экран"),
        ["BHS.CRG.Application/Templates/DocumentTemplateInvalidator.cs|var docs = await objRepo.GetDocumentsOfTypeAsync(documentTypeId, ct);"] =
            Documents("документы типа — сброс вывода при правке шаблона"),
        ["BHS.CRG.Application/Templates/DocumentTemplateInvalidator.cs|var docs = await objRepo.GetDocumentsOfTypeAsync(tpl.DocumentTypeId, ct);"] =
            Documents("документы типа — сброс вывода при правке шаблона"),
        ["BHS.CRG.Application/Templates/Handlers.cs|var docs = await objRepo.GetDocumentsOfTypeAsync(q.DocumentTypeId, ct);"] =
            Documents("документы типа — сброс вывода при правке шаблона"),
        ["BHS.CRG.Application/Templates/Handlers.cs|var docs = await objRepo.GetDocumentsOfTypeAsync(t.DocumentTypeId, ct);"] =
            Documents("документы типа — сброс вывода при правке шаблона"),

        ["BHS.CRG.Infrastructure/Backup/BackupService.Manifest.cs|? await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Documents("копия: документы"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Manifest.cs|var commonDataEntries = await db.DomainObjects.AsNoTracking().Where(o => o.Facet == null).ToListAsync(ct);"] =
            Shown("копия: все записи, признак — в записи манифеста"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.CommonData.cs|var existingIds = await db.DomainObjects.Select(e => e.Id).ToHashSetAsync(ct);"] =
            Service("восстановление копии: сверка идентификаторов"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.CommonData.cs|var presentIds = await db.DomainObjects"] =
            Service("восстановление копии: сверка идентификаторов"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.WorkPlan.cs|var objects = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);"] =
            Service("восстановление копии: сверка идентификаторов"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.cs|var existing = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);"] =
            Service("восстановление копии: сверка идентификаторов"),
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.cs|var ownerIds = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);"] =
            Service("восстановление копии: сверка идентификаторов"),
        ["BHS.CRG.Infrastructure/DataFixups/ImageSizeToInstanceFixup.cs|var objects = await db.DomainObjects.ToListAsync(ct);"] =
            Service("разовая починка данных"),
        ["BHS.CRG.Infrastructure/DataSets/DataSetBindingService.cs|typeByDocument = await db.DomainObjects.AsNoTracking()"] =
            Documents("типы документов-владельцев привязок"),
        ["BHS.CRG.Infrastructure/DataSets/DataSetBindingService.cs|var owner = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Seen("владелец привязки набора — по идентификатору"),
        ["BHS.CRG.Infrastructure/DataSets/DataSetBindingService.cs|var ownerIsDocument = await db.DomainObjects.AsNoTracking()"] =
            Seen("владелец привязки набора — по идентификатору"),
        ["BHS.CRG.Infrastructure/DataSets/DataSetSourceService.cs|typeByDocument = await db.DomainObjects.AsNoTracking()"] =
            Documents("типы документов-владельцев привязок"),
        ["BHS.CRG.Infrastructure/DataSets/DataSetSourceService.cs|var owners = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Seen("кто пользуется источником: владельцы привязок по идентификаторам"),
        ["BHS.CRG.Infrastructure/DataSets/DomainObjectsProvider.cs|db.DomainObjects.AsNoTracking().Where(o => o.Facet == null &&"] =
            Shown("системный набор объектов: архивные на месте, колонка «ВАрхиве» — реестр выпущенного документа не меняется"),
        ["BHS.CRG.Infrastructure/DataSets/DomainObjectsProvider.cs|var batch = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Seen("цепочка базовых экземпляров строк набора: сохранённые ссылки"),
        ["BHS.CRG.Infrastructure/DataSets/MaterializeByIdMode.cs|var docs = await db.DomainObjects.AsNoTracking()"] =
            Seen("подписи ссылок на ДОКУМЕНТЫ по идентификатору; запись общих данных здесь названа «не документ» при любом состоянии"),
        ["BHS.CRG.Infrastructure/DataSets/SetDocumentsProvider.cs|var documents = (await objects.GetSetDocumentsAsync(setId, tracked: false, ct))"] =
            Documents("документы — строки системного набора"),
        ["BHS.CRG.Infrastructure/DataSets/SubtreeDocumentsProvider.cs|var documents = await objects.GetDocumentsInSetsAsync([.. setById.Keys], ct);"] =
            Documents("документы — строки системного набора"),
        ["BHS.CRG.Infrastructure/Documents/DocumentSearchService.cs|FROM domain_objects o"] =
            Documents("поиск документов: соединение с фасетой"),
        ["BHS.CRG.Infrastructure/Email/DocumentSetEmailService.cs|var instance = await instanceRepo.GetByIdAsync(instanceId, ct) ?? throw new NotFoundException(\"Документ не найден.\");"] =
            Documents("документ по идентификатору"),
        ["BHS.CRG.Infrastructure/Generation/DataSetResolver.cs|typeByDocument = await db.DomainObjects.AsNoTracking()"] =
            Documents("типы документов-владельцев привязок"),
        ["BHS.CRG.Infrastructure/Generation/DocumentSetAssemblyService.cs|var included = (await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct))"] =
            Documents("документы комплекта для сборки"),
        ["BHS.CRG.Infrastructure/Generation/DocumentSetAssemblyService.cs|var ordered = (await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct))"] =
            Documents("документы комплекта для сборки"),
        ["BHS.CRG.Infrastructure/Generation/DomainSnapshotService.cs|var counts = await objects.CountDocumentsInSetsAsync(setIds, ct);"] =
            Documents("MCP: документы комплектов и их счётчики").X(2),
        ["BHS.CRG.Infrastructure/Generation/DomainSnapshotService.cs|var docs = await objects.GetSetDocumentsAsync(setId, tracked: false, ct);"] =
            Documents("MCP: документы комплектов и их счётчики"),
        ["BHS.CRG.Infrastructure/Generation/DomainSnapshotService.cs|var entries = await domainObjects.FindAsync(e => e.Facet == null"] =
            Shown("MCP: список записей каталога — архивные на месте, признак в ответе"),
        ["BHS.CRG.Infrastructure/Generation/DomainSnapshotService.cs|var entry = await domainObjects.GetByIdAsync(entryId, ct);"] =
            Shown("MCP: запись каталога по идентификатору — признак в ответе"),
        ["BHS.CRG.Infrastructure/Generation/DomainSnapshotService.cs|var found = await domainObjects.FindAsync(o => ids.Contains(o.Id), ct);"] =
            Documents("MCP: документы комплектов и их счётчики"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var baseObj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == baseId, ct);"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var baseObj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var obj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var obj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var profileObj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync("] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var refObj = await db.DomainObjects.AsNoTracking()"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/EntityResolver.cs|var typeId = await db.DomainObjects.Where(o => o.Id == entryId)"] =
            Seen("генерация разрешает СОХРАНЁННЫЕ ссылки: документ с архивным поставщиком печатается как прежде, без пометки"),
        ["BHS.CRG.Infrastructure/Generation/LevelProfileService.cs|if (currentFk is { } fk && await db.DomainObjects.AsNoTracking().AnyAsync(o => o.Id == fk, ct))"] =
            Seen("профиль уровня: жив ли уже назначенный"),
        ["BHS.CRG.Infrastructure/Generation/LevelProfileService.cs|var existing = await db.DomainObjects.FirstOrDefaultAsync("] =
            Choice("назначение профилем — выбор: архивная запись под профиль не берётся", "Под_профиль_уровня_архивная_запись_не_берётся"),
        ["BHS.CRG.Infrastructure/Generation/ObjectResolver.cs|var candidates = await db.DomainObjects.AsNoTracking()"] =
            Shown("резолвер «строка → объект»: находит и архивные, состояние в ответе, действующая побеждает архивную"),
        ["BHS.CRG.Infrastructure/Maintenance/ImageBlobMigration.cs|var blobSql = \"SELECT \\\"Id\\\" FROM domain_objects WHERE \\\"Data\\\"::text LIKE '%\\\"$type\\\": \\\"image\\\"%' OR \\\"Data\\\"::text LIKE '%\\\"$type\\\":\\\"image\\\"%'\";"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/ImageBlobMigration.cs|await db.DomainObjects.AsNoTracking().Where(o => o.Id == id)"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/ImageBlobMigration.cs|UPDATE domain_objects SET \"Data\" = {json}::jsonb, \"UpdatedAt\" = {DateTimeOffset.UtcNow}"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/ImageBlobMigration.cs|var sql = \"SELECT \\\"Id\\\" FROM domain_objects WHERE \\\"Data\\\"::text LIKE '%data:image%'\";"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/MaterialLabelBackfill.cs|foreach (var data in await db.DomainObjects.AsNoTracking().Select(o => o.Data).ToListAsync(ct))"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/OrphanObjectCleanup.cs|SELECT count(*)::int AS \"Value\" FROM domain_objects"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/OrphanObjectCleanup.cs|await db.DomainObjects.Where(o => objIds.Contains(o.Id)).ExecuteDeleteAsync(ct);"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Maintenance/OrphanObjectCleanup.cs|var objectIds = await db.DomainObjects"] =
            Service("обслуживание: перенос картинок, уборка сирот, разовые обходы"),
        ["BHS.CRG.Infrastructure/Persistence/Configurations/DomainObjectConfiguration.cs|b.ToTable(\"domain_objects\");"] =
            Service("объявление таблицы — не чтение"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|.SqlQuery<long>($\"\"\"SELECT xmin::text::bigint AS \"Value\" FROM domain_objects WHERE \"Id\" = {entry.Id} FOR UPDATE\"\"\")"] =
            Service("правка записи: версия строки читается под блокировкой, архивную запись правят так же (issue #1214)"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|.SqlQuery<Guid>($\"\"\"SELECT \"Id\" AS \"Value\" FROM domain_objects WHERE \"Id\" = ANY({wanted}) ORDER BY \"Id\" FOR NO KEY UPDATE\"\"\")"] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|var ids = await Db.Set<DomainObject>().Where(which).Select(o => o.Id).ToListAsync(ct);"] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|if (Db.Set<DomainObject>().Local.FindEntry(id) is { } entry)"] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|var objects = await Db.Set<DomainObject>()"] =
            Service("чтение под блокировкой строки для писателя-преобразователя: архивную запись переносят и чинят так же (issue #1232)"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|=> Db.Set<DomainObject>()"] =
            Seen("чтение по идентификатору: что делать с архивной, решает звавший"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|=> await Db.Set<DomainObject>()"] =
            Documents("документы комплектов и их счётчики").X(2),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|return await Db.Set<DomainObject>()"] =
            Shown("названия уже стоящих ссылок"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|var q = Db.Set<DomainObject>()"] =
            Documents("документы комплектов и их счётчики"),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|var query = Db.Set<DomainObject>()"] =
            Choice("кандидаты на выбор", nameof(ArchiveReadPurposeTests.Поиск_на_выбор_архив_не_отдаёт_но_называет_числом_а_по_идентификаторам_находит)),
        ["BHS.CRG.Infrastructure/Persistence/DomainObjectRepository.cs|var rows = await Db.Set<DomainObject>()"] =
            Documents("документы комплектов и их счётчики").X(2),
        ["BHS.CRG.Infrastructure/Persistence/MigrationCensus.cs|\"SELECT count(*) FROM domain_objects o WHERE o.\\\"ScopeId\\\" IS NOT NULL AND (\" +"] =
            Service("перепись перед миграцией"),
        ["BHS.CRG.Infrastructure/Persistence/MigrationCensus.cs|private const string Objects = \"domain_objects\";"] =
            Service("перепись перед миграцией"),
        ["BHS.CRG.Infrastructure/Persistence/ModuleLostReferenceScan.cs|return await db.DomainObjects.AsNoTracking()"] =
            Shown("состояние ссылки модуля: это и есть вопрос об архиве"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|UPDATE domain_objects o"] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|db.DomainObjects.AsNoTracking().AnyAsync("] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|await db.DomainObjects"] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|db.DomainObjects"] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|var seen = await db.DomainObjects.AsNoTracking().Where(o => o.Id == id)"] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/RecordArchive.cs|var target = db.DomainObjects.Where(o => o.Id == id && o.Facet == null && (o.ArchivedAt != null) != archived);"] =
            Service("сама служба архива"),
        ["BHS.CRG.Infrastructure/Persistence/ReferenceIndex.cs|=> QueryAsync(Sql(\"domain_objects\", \"Data\"), ids, ct);"] =
            Seen("индекс ссылок: кто ссылается на запись; архивный держатель держит так же"),
        ["BHS.CRG.Infrastructure/Reconciliation/ProblemAttributionService.cs|: await db.DomainObjects.AsNoTracking()"] =
            Seen("владельцы привязок по идентификаторам: к какому уровню отнести находку"),

        ["BHS.CRG.Modules.Costs/Endpoints/AllocationMatrixEndpoints.cs|Existing = (await references.StatesAsync(ReferenceTarget.Record, articles, ct))"] =
            Seen("предпросмотр разноски: есть ли запись вовсе; архивная статья — на месте, признак несёт справочник статей"),
        ["BHS.CRG.Modules.Costs/Endpoints/AllocationPlaces.cs|await catalog.ListAsync(CostsRecordTypes.ArticleCode, RecordsFor.Display, ct) is { } entries"] =
            Shown("справочник статей: названия стоящих частей, проверка новой части и экран справочника — признак в ответе"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceDesk.cs|var records = await targets.StatesAsync(ReferenceTarget.Record,"] =
            Shown("состояние ссылок счёта: «в архиве» у сторон, строк и частей разноски"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceDesk.cs|: await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, parties, ct);"] =
            Seen("названия сторон счёта — уже стоящих; признак рядом, из состояния ссылок"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceListEndpoint.cs|var organizations = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, RecordsFor.Display, ct);"] =
            Shown("реестр счетов: названия поставщиков уже заведённых счетов, признак значком"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceEndpoints.cs|var refs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, ids, ct);"] =
            Seen("названия позиций в стоящих строках счёта; признак строке даёт состояние ссылок"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceEndpoints.cs|var states = await targets.StatesAsync(ReferenceTarget.Record, [.. named.Select(p => p.Id!.Value)], ct);"] =
            Seen("новая сторона счёта: есть ли запись вовсе; архивную отвергает правило новых ссылок следом"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceLineEndpoints.cs|if (id is { } organization && await catalog.GetAsync(organization, ct) is null)"] =
            Seen("переход «разобран»: стороны на месте; архивная — на месте, счёт с ней разбирается"),
        ["BHS.CRG.Modules.Costs/Endpoints/NewReferences.cs|if (await catalog.RefsAsync(typeCode, fresh, ct) is not { } found) return null;"] =
            Choice("правило новых ссылок модуля: сторона счёта, позиция строки счёта и накладной — архивная отвергается",
                nameof(InvoiceArchiveTests.Счёт_с_архивным_поставщиком_читается_и_правится_а_новым_поставщиком_архивный_не_ставится),
                nameof(InvoiceArchiveTests.Строка_счёта_с_архивной_позицией_остаётся_а_новая_строка_её_не_принимает),
                nameof(InvoiceArchiveTests.Накладная_стоявшую_архивную_позицию_держит_новую_не_принимает)),
        ["BHS.CRG.Modules.Costs/Endpoints/NomenclatureEndpoints.cs|var found = await catalog.SearchAsync(CostsRecordTypes.NomenclatureCode, query, Limit + 1, ct)"] =
            Choice("выбор позиции в строке счёта и накладной", nameof(ArchiveReadPurposeTests.Выбор_позиции_номенклатуры_в_счёте_архивную_не_предлагает)),
        ["BHS.CRG.Modules.Costs/Endpoints/SupplierMatchEndpoints.cs|var refs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, positions, ct);"] =
            Choice("подстановка запомненного в строку счёта: архивная позиция названа с причиной, но не подставляется",
                nameof(SupplierMatchTests.Архивная_позиция_не_подставляется_и_это_названо)),
        ["BHS.CRG.Modules.Costs/Endpoints/SupplierMatchListEndpoints.cs|var supplierRefs = await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, [.. perSupplier.Select(s => s.Id)], ct);"] =
            Seen("список соответствий: названия поставщиков, у которых они уже есть; архивный — с признаком"),
        ["BHS.CRG.Modules.Costs/Endpoints/SupplierMatchListEndpoints.cs|var positionRefs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, positionIds, ct);"] =
            Seen("список соответствий: позиции, на которые они уже ведут; архивная названа причиной «не подставляется»"),
        ["BHS.CRG.Modules.Costs/Endpoints/SupplierMatchListEndpoints.cs|var supplierRefs = await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, [match.SupplierId], ct);"] =
            Seen("имя поставщика соответствия — для записи журнала и ответа правки"),
        ["BHS.CRG.Modules.Costs/Endpoints/SupplierMatchListEndpoints.cs|var positionRefs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, asked, ct);"] =
            Seen("названия прежней и новой позиции соответствия — для журнала; новую позицию судит правило новых ссылок"),
        ["BHS.CRG.Modules.Costs/Endpoints/OrganizationEndpoints.cs|var entries = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, records, ct)"] =
            ByPurpose("организации для формы счёта: выбор скрывает архивные, показ отдаёт с признаком",
                nameof(InvoiceArchiveTests.Список_организаций_на_выбор_архивную_скрывает_а_на_показ_отдаёт_с_признаком)),
        ["BHS.CRG.Modules.Costs/Endpoints/SiteCostsEndpoints.cs|: (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, RecordsFor.Display, ct))?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];"] =
            Seen("названия поставщиков уже заведённых счетов"),
        ["BHS.CRG.Modules.Costs/Endpoints/WaybillEndpoints.cs|return (await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, ids, ct))"] =
            Shown("позиции стоящих строк накладной и перечня отпущенного: название и признак"),
        ["BHS.CRG.Modules.Costs/Tables/InvoiceTableRows.Prepare.cs|var names = (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, RecordsFor.Display, ct))"] =
            Seen("названия поставщиков уже заведённых счетов"),
    };
}
