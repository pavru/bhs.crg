using BHS.CRG.Application.Common;
using BHS.CRG.Application.QualityDocs;
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
    IRepository<Domain.Catalog.PrimitiveType> primitiveRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver
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
        // Документы качества подмешиваем и здесь (issue #585): без этого шага проверка не увидела бы
        // ни привязанных сертификатов, ни их отсутствия — и ответила бы про документ не то, что
        // покажет выпуск.
        await qualityLinkResolver.InjectAsync(context, view, ct);
        await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
        await entityResolver.ResolveComputedFieldsAsync(context, view, diagnostics, ct); // issue #368
        ResolutionScanner.ScanLeftoverRefs(context, diagnostics);
        // Полнота обязательных (issue #296, фаза 0b) — та же проверка, что при генерации.
        var byId = (await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.CompositeTypeId, byId);
        ResolutionScanner.ScanMissingRequired(context, fields, diagnostics);

        // Соответствие значений объявленному типу (issue #461) — предупреждениями: данные накоплены,
        // и половина документов перестала бы выпускаться в тот же день.
        var primitives = (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        ValueTypeScanner.Scan(context, fields, byId, primitives, diagnostics);

        // Материалы без документа качества (issue #585) — предупреждениями, как и при выпуске.
        var allTypes = byId.Values.ToList();
        QualityLinkScanner.Scan(context, MaterialIdentity.KeysOf(allTypes),
            MaterialIdentity.QualityDocFieldOf(allTypes), diagnostics);

        return diagnostics;
    }
}
