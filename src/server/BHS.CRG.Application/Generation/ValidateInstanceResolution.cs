using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Прогоняет полный цикл разрешения ссылок для экземпляра (как при генерации),
/// но вместо генерации возвращает собранную диагностику. Используется для проверки
/// «по требованию» из UI.
/// </summary>
public record ValidateInstanceResolutionQuery(Guid InstanceId) : IRequest<IReadOnlyList<ResolutionDiagnostic>>;

public class ValidateInstanceResolutionHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<DocumentType> docTypeRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver
) : IRequestHandler<ValidateInstanceResolutionQuery, IReadOnlyList<ResolutionDiagnostic>>
{
    public async Task<IReadOnlyList<ResolutionDiagnostic>> Handle(ValidateInstanceResolutionQuery q, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(q.InstanceId, ct)
            ?? throw new KeyNotFoundException($"DocumentInstance {q.InstanceId} not found");

        var diagnostics = new List<ResolutionDiagnostic>();
        var view = DocumentView.From(instance);
        var context = await entityResolver.ResolveAsync(view, ct);
        await dataSetResolver.InjectAsync(context, view, diagnostics, ct);
        await entityResolver.ApplyDefaultsAsync(context, view, ct);
        await entityResolver.ResolveEnumLabelsAsync(context, view, ct);
        await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
        ResolutionScanner.ScanLeftoverRefs(context, diagnostics);
        // Полнота обязательных (issue #296, фаза 0b) — та же проверка, что при генерации.
        var byId = (await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        ResolutionScanner.ScanMissingRequired(context, DocumentTypeSchemaReader.EffectiveFields(instance.CompositeTypeId, byId), diagnostics);
        return diagnostics;
    }
}
