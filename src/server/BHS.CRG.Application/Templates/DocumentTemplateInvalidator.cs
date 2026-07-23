using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Application.Templates;

/// <summary>
/// Инвалидация вывода документов при изменении шаблона (issue #362, фаза 2). Когда содержимое
/// шаблона (или эффективный default-active) меняется, ранее сгенерированные PDF устаревают —
/// документы сбрасываются в <see cref="Domain.Documents.DocumentStatus.Draft"/>, их файлы удаляются.
/// Пины НЕ переставляются («запиннут на конкретную версию» — семантика сохраняется).
/// </summary>
public interface IDocumentTemplateInvalidator
{
    /// <summary>
    /// Содержимое версии изменено на месте (Ctrl+S). Сбрасывает документы, запиннутые на эту
    /// версию; а если версия — активная-дефолтная, то и no-pin документы этого типа (они
    /// резолвятся в default-active). Возвращает число реально сброшенных документов.
    /// </summary>
    Task<int> OnTemplateContentChangedAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Эффективный default-active шаблон типа сменился (смена дефолта или новая версия в дефолтной
    /// группе). Сбрасывает no-pin документы этого типа. Возвращает число сброшенных документов.
    /// </summary>
    Task<int> OnDefaultChangedAsync(Guid documentTypeId, CancellationToken ct = default);
}

public class DocumentTemplateInvalidator(
    IRepository<Template> templateRepo,
    IDomainObjectRepository objRepo,
    IBlobStorage blobStorage) : IDocumentTemplateInvalidator
{
    public async Task<int> OnTemplateContentChangedAsync(Guid templateId, CancellationToken ct = default)
    {
        var tpl = await templateRepo.GetByIdAsync(templateId, ct);
        if (tpl is null) return 0;

        var docs = await objRepo.GetDocumentsOfTypeAsync(tpl.DocumentTypeId, ct);
        var isDefaultActive = tpl.IsDefault && tpl.IsActive;
        var affected = docs
            .Where(o => o.PinsTemplate(templateId) || (isDefaultActive && o.HasNoTemplatePin))
            .ToList();
        return await ResetAllAsync(affected, ct);
    }

    public async Task<int> OnDefaultChangedAsync(Guid documentTypeId, CancellationToken ct = default)
    {
        var docs = await objRepo.GetDocumentsOfTypeAsync(documentTypeId, ct);
        var affected = docs.Where(o => o.HasNoTemplatePin).ToList();
        return await ResetAllAsync(affected, ct);
    }

    // Сброс: только не-черновики (у остальных сбрасывать нечего). SaveChanges один раз (атомарно),
    // удаление блобов — ПОСЛЕ коммита (конвенция reset-хендлеров: откат не осиротит файлы).
    private async Task<int> ResetAllAsync(List<DomainObject> objs, CancellationToken ct)
    {
        var blobs = new List<string>();
        var count = 0;
        foreach (var o in objs)
        {
            if (o.Status == Domain.Documents.DocumentStatus.Draft) continue; // уже черновик — нечего сбрасывать
            blobs.AddRange(o.ResetToDraft());
            objRepo.Update(o);
            count++;
        }
        if (count == 0) return 0;
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return count;
    }
}
