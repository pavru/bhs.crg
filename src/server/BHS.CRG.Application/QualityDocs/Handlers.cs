using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

public class QualityDocHandlers(
    IRepository<QualityDocument> repo,
    IRepository<MaterialQualityLink> linkRepo,
    IRepository<DocumentType> typeRepo
) :
    IRequestHandler<CreateQualityDocumentCommand, QualityDocument>,
    IRequestHandler<UpdateQualityDocumentCommand, QualityDocument>,
    IRequestHandler<SetQualityDocScanCommand, QualityDocument>,
    IRequestHandler<DeleteQualityDocumentCommand>,
    IRequestHandler<GetQualityDocumentQuery, QualityDocument?>,
    IRequestHandler<ListQualityDocumentsQuery, IReadOnlyList<QualityDocument>>,
    IRequestHandler<SetMaterialLinksCommand, int>,
    IRequestHandler<RemoveMaterialLinkCommand>,
    IRequestHandler<RemoveMaterialLinksCommand, int>,
    IRequestHandler<ListMaterialLinksQuery, IReadOnlyList<MaterialLinkRow>>
{
    public async Task<QualityDocument> Handle(CreateQualityDocumentCommand cmd, CancellationToken ct)
    {
        await EnsureNameFreeAsync(cmd.DisplayName, cmd.Scope, cmd.ScopeId, exceptId: null, ct);
        var doc = QualityDocument.Create(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites, cmd.Scope, cmd.ScopeId, cmd.Source);
        doc.SetScan(cmd.ScanBlobPath, cmd.ScanFileName, cmd.ScanMimeType);
        await repo.AddAsync(doc, ct);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<QualityDocument> Handle(UpdateQualityDocumentCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException($"QualityDocument {cmd.Id} not found");
        // Проверяем ТОЛЬКО смену имени. Дубли уже есть в живой базе (ради них issue #588 и заведена),
        // и запрет на сохранение с НЕИЗМЕНЁННЫМ именем означал бы, что такой документ нельзя больше
        // ни отредактировать, ни распознать, пока его не переименуют.
        if (!string.Equals(doc.DisplayName.Trim(), (cmd.DisplayName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            await EnsureNameFreeAsync(cmd.DisplayName, doc.Scope, doc.ScopeId, exceptId: doc.Id, ct);
        doc.Update(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites);
        repo.Update(doc);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    /// <summary>
    /// Имя документа качества уникально В СВОЕЙ ОБЛАСТИ (issue #588).
    ///
    /// Живой случай: два сертификата назывались одинаково — «EKF — автоматические выключатели», а
    /// внутри разные номера (RU C-CN.HA46.B.06753/23 и ЕАЭС RU C-CN.АД07.B.05521/23), разные органы
    /// и разные области продукции (AV-125 против AV-6/AV-10). В выпадающем списке они неразличимы, и
    /// человек выбирает вслепую.
    ///
    /// Проверяем только ручные пути (создание и правка формы). Импорт из интернета сюда не заходит:
    /// у него своя защита от дублей — по URL, а имя приходит из чужой выдачи, и отказывать в импорте
    /// из-за совпадения заголовка значило бы ломать рабочий сценарий ради косметики.
    /// </summary>
    private async Task EnsureNameFreeAsync(
        string displayName, CatalogScope scope, Guid? scopeId, Guid? exceptId, CancellationToken ct)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length == 0) return; // пустое имя — забота валидации формы, не этой проверки

        var sameScope = await repo.FindAsync(d => d.Scope == scope && d.ScopeId == scopeId, ct);
        var taken = sameScope.Any(d =>
            (exceptId is null || d.Id != exceptId)
            && string.Equals(d.DisplayName.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (taken)
            throw new ConflictException(
                $"Документ качества с именем «{name}» в этой области уже есть. Имена должны различаться: "
                + "иначе при выборе из списка непонятно, какой из документов берут.");
    }

    public async Task<QualityDocument> Handle(SetQualityDocScanCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException($"QualityDocument {cmd.Id} not found");
        doc.SetScan(cmd.ScanBlobPath, cmd.ScanFileName, cmd.ScanMimeType);
        repo.Update(doc);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    public async Task Handle(DeleteQualityDocumentCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException($"QualityDocument {cmd.Id} not found");
        // удаляем связи, ссылающиеся на документ
        var links = await linkRepo.FindAsync(l => l.QualityDocumentId == cmd.Id, ct);
        foreach (var l in links) linkRepo.Remove(l);
        repo.Remove(doc);
        await linkRepo.SaveChangesAsync(ct);
        await repo.SaveChangesAsync(ct);
    }

    public Task<QualityDocument?> Handle(GetQualityDocumentQuery q, CancellationToken ct)
        => repo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<QualityDocument>> Handle(ListQualityDocumentsQuery q, CancellationToken ct)
    {
        var scope = q.Scope;
        var scopeId = q.ScopeId;
        var items = await repo.FindAsync(d =>
            (!scope.HasValue || d.Scope == scope.Value) &&
            (!scopeId.HasValue || d.ScopeId == scopeId.Value), ct);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            items = items.Where(d => d.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return items.OrderBy(d => d.DisplayName).ToList();
    }

    public async Task<int> Handle(SetMaterialLinksCommand cmd, CancellationToken ct)
    {
        var existing = await linkRepo.FindAsync(l => l.Scope == cmd.Scope && l.ScopeId == cmd.ScopeId, ct);
        var byKey = existing.ToDictionary(l => l.MaterialKey);
        // Ключи, созданные ЭТИМ пакетом: их нельзя отдавать в Update — сущность ещё в состоянии Added,
        // и EF выпустил бы UPDATE по несуществующей строке («expected to affect 1 row, actually 0»).
        var created = new HashSet<string>();
        var count = 0;
        foreach (var material in cmd.Materials)
        {
            // Канонизация ПО КОМПОНЕНТАМ: ключ пришёл составным (#582), и нормализация его целиком
            // схлопнула бы пустые слоты — связка легла бы под ключ, которого резолвер не построит.
            var key = IdentityKey.Canonicalize(material.Key);
            if (IdentityKey.IsEmpty(key)) continue;
            if (byKey.TryGetValue(key, out var link))
            {
                link.Retarget(cmd.QualityDocumentId);
                // Метку обновляем, но пустой не затираем: перепривязка с экрана контроля идёт без
                // имени, и она не должна отнимать имя, добытое при первой привязке.
                link.DescribeMaterial(material.Label);
                if (!created.Contains(key)) linkRepo.Update(link);
            }
            else
            {
                var link2 = MaterialQualityLink.Create(
                    cmd.Scope, cmd.ScopeId, key, cmd.QualityDocumentId, material.Label);
                await linkRepo.AddAsync(link2, ct);
                // Кладём созданное в карту: два материала ОДНОГО пакета могут нормализоваться в один
                // ключ («шт.» и «шт»), и без этого оба ушли бы в Add — с уникальным индексом (#554)
                // SaveChanges бросил бы и откатил ВЕСЬ пакет. Достижимо из-за клиентской копии
                // нормализатора, которая может разойтись с серверной.
                byKey[key] = link2;
                created.Add(key);
            }
            count++;
        }
        await linkRepo.SaveChangesAsync(ct);
        return count;
    }

    public async Task Handle(RemoveMaterialLinkCommand cmd, CancellationToken ct)
    {
        var link = await linkRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        linkRepo.Remove(link);
        await linkRepo.SaveChangesAsync(ct);
    }

    public async Task<int> Handle(RemoveMaterialLinksCommand cmd, CancellationToken ct)
    {
        if (cmd.Ids.Count == 0) return 0;
        var ids = cmd.Ids.Distinct().ToList();
        var links = await linkRepo.FindAsync(l => ids.Contains(l.Id), ct);
        foreach (var link in links) linkRepo.Remove(link);
        await linkRepo.SaveChangesAsync(ct);
        // Возвращаем СНЯТОЕ, а не запрошенное: часть строк могла исчезнуть в параллельной сессии, и
        // «сняли 69» при фактических 68 было бы неправдой в отчёте пользователю.
        return links.Count;
    }

    public async Task<IReadOnlyList<MaterialLinkRow>> Handle(ListMaterialLinksQuery q, CancellationToken ct)
    {
        // Область необязательна: без неё отдаём связки ВСЕХ областей (экран контроля смотрит поперёк).
        // Но заданный ScopeId уважаем и без Scope — иначе запрос «связки этого комплекта» молча
        // вернул бы вообще все, с кодом 200.
        var links = (q.Scope, q.ScopeId) switch
        {
            ({ } scope, var scopeId) => await linkRepo.FindAsync(l => l.Scope == scope && l.ScopeId == scopeId, ct),
            (null, { } scopeId) => await linkRepo.FindAsync(l => l.ScopeId == scopeId, ct),
            _ => await linkRepo.GetAllAsync(ct),
        };
        if (links.Count == 0) return [];

        // Имя документа — вторым запросом по нужным id, а не обходом в памяти всей библиотеки.
        var docIds = links.Select(l => l.QualityDocumentId).Distinct().ToList();
        var docs = await repo.FindAsync(d => docIds.Contains(d.Id), ct);
        var byId = docs.ToDictionary(d => d.Id);

        // Вид документа берём из РЕЕСТРА ТИПОВ, а не из реквизита «ТипДокумента»: реквизит — свободный
        // текст из распознавания, в живой базе три вида записаны шестью способами («Сертификат»,
        // «СЕРТИФИКАТ СООТВЕТСТВИЯ», …), и у одного документа он ПРОТИВОРЕЧИТ собственному типу.
        // Тот же источник, что и у снапшота (DomainSnapshotService.ListMaterialQualityLinksAsync).
        var typeIds = docs.Select(d => d.DocumentTypeId).Distinct().ToList();
        var typeName = (await typeRepo.FindAsync(t => typeIds.Contains(t.Id), ct))
            .ToDictionary(t => t.Id, t => t.Name);

        return links
            .Select(l =>
            {
                byId.TryGetValue(l.QualityDocumentId, out var doc);
                return new MaterialLinkRow(
                    l.Id, l.MaterialKey, l.MaterialLabel, l.Scope, l.ScopeId, l.QualityDocumentId,
                    // Документа может не быть только у связки, пережившей ручную чистку БД: внешний
                    // ключ с каскадом (#554) такие больше не оставляет. Честнее пустого имени.
                    doc?.DisplayName ?? "(документ удалён)",
                    doc is not null && typeName.TryGetValue(doc.DocumentTypeId, out var type) ? type : null,
                    l.CreatedAt, l.UpdatedAt);
            })
            .OrderBy(r => r.QualityDocumentName)
            .ThenBy(r => r.MaterialLabel ?? r.MaterialKey)
            .ToList();
    }
}
