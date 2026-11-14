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
    /// версию; а если версия — шаблон документов без выбора (<see cref="Generation.TemplateChoice.Fallback"/>:
    /// по умолчанию или, без него, самый ранний), то и no-pin документы этого типа. Возвращает число
    /// реально сброшенных документов.
    /// </summary>
    Task<int> OnTemplateContentChangedAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Эффективный default-active шаблон типа сменился (смена дефолта). Сбрасывает no-pin документы
    /// этого типа. Возвращает число сброшенных документов.
    /// </summary>
    Task<int> OnDefaultChangedAsync(Guid documentTypeId, CancellationToken ct = default);

    /// <summary>Шаблон, который сейчас берут документы типа без выбора, — снимок до изменения шаблонов
    /// (для <see cref="OnFallbackMayHaveChangedAsync"/>). Null — действующих нет.</summary>
    Task<Guid?> FallbackOfAsync(Guid documentTypeId, CancellationToken ct = default);

    /// <summary>
    /// Шаблоны типа изменились (новая версия, удаление версии). Если документы без выбора теперь берут
    /// не <paramref name="before"/>, их PDF напечатан другим шаблоном — no-pin документы сбрасываются.
    /// Ключ запасного выбора — возраст шаблона (D2), и удаление старейшей версии сдвигает его так же, как
    /// смена шаблона по умолчанию. Возвращает число сброшенных документов.
    /// </summary>
    Task<int> OnFallbackMayHaveChangedAsync(Guid documentTypeId, Guid? before, CancellationToken ct = default);
}

public class DocumentTemplateInvalidator(
    IRepository<Template> templateRepo,
    ITemplateNames names,
    IDomainObjectRepository objRepo,
    IBlobStorage blobStorage) : IDocumentTemplateInvalidator
{
    public async Task<int> OnTemplateContentChangedAsync(Guid templateId, CancellationToken ct = default)
    {
        var tpl = await templateRepo.GetByIdAsync(templateId, ct);
        if (tpl is null) return 0;

        var docs = await objRepo.GetDocumentsOfTypeAsync(tpl.DocumentTypeId, ct);
        // Шаблон no-pin документов — тем же правилом, что у генерации: без «по умолчанию» это самый
        // ранний шаблон, а не никакой (issue #1401).
        var isFallback = await FallbackOfAsync(tpl.DocumentTypeId, ct) == templateId;
        var affected = docs
            .Where(o => o.PinsTemplate(templateId) || (isFallback && o.HasNoTemplatePin))
            .ToList();
        return await ResetAllAsync(affected, ct);
    }

    public async Task<Guid?> FallbackOfAsync(Guid documentTypeId, CancellationToken ct = default)
        => Generation.TemplateChoice.Fallback(
            await templateRepo.FindAsync(t => t.DocumentTypeId == documentTypeId && t.IsActive, ct),
            await names.FirstCreatedAsync(documentTypeId, ct))?.Id;

    public async Task<int> OnFallbackMayHaveChangedAsync(Guid documentTypeId, Guid? before, CancellationToken ct = default)
        => await FallbackOfAsync(documentTypeId, ct) == before ? 0 : await OnDefaultChangedAsync(documentTypeId, ct);

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
