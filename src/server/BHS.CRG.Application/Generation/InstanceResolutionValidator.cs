using BHS.CRG.Application.Common;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Справочники схемы, общие для всех документов одного прогона: типы документов и примитивные типы.
/// Отдельная запись, а не чтение внутри проверки, потому что проверка бывает пакетной: сверка по
/// комплекту (#589) прогоняла один и тот же <c>SELECT * FROM document_types</c> и
/// <c>SELECT * FROM primitive_types</c> НА КАЖДЫЙ документ — на комплекте в полсотни документов это
/// сотня одинаковых запросов подряд (issue #628).
///
/// Кэшем это намеренно не сделано: кэш пришлось бы инвалидировать при правке типа, и «проверил после
/// изменения схемы, а увидел прежнюю» — отказ куда неприятнее лишнего запроса. Здесь область жизни
/// справочников задаёт ВЫЗЫВАЮЩИЙ: одиночная проверка читает их себе, пакетная — один раз на прогон.
/// </summary>
public record SchemaCatalog(
    IReadOnlyDictionary<Guid, DocumentType> DocumentTypes,
    IReadOnlyDictionary<Guid, PrimitiveType> Primitives)
{
    /// <summary>Типы списком — в таком виде их ждут <see cref="MaterialIdentity"/>-хелперы.</summary>
    public IReadOnlyList<DocumentType> AllTypes { get; } = [.. DocumentTypes.Values];
}

/// <summary>
/// Полный цикл разрешения ссылок для экземпляра — как при генерации, но вместо документа
/// возвращается собранная диагностика. Пайплайн здесь ОДИН на всех: и проверка «по требованию» из
/// UI, и сверка по комплекту ходят сюда, чтобы не разойтись в том, что считается проблемой.
/// </summary>
public interface IInstanceResolutionValidator
{
    /// <summary>Прочитать справочники схемы — один раз на прогон (см. <see cref="SchemaCatalog"/>).</summary>
    Task<SchemaCatalog> LoadCatalogAsync(CancellationToken ct);

    /// <summary>Проверить один экземпляр справочниками, прочитанными заранее.</summary>
    Task<IReadOnlyList<ResolutionDiagnostic>> ValidateAsync(Guid instanceId, SchemaCatalog catalog, CancellationToken ct);
}

public class InstanceResolutionValidator(
    IRepository<DomainObject> instanceRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver
) : IInstanceResolutionValidator
{
    public async Task<SchemaCatalog> LoadCatalogAsync(CancellationToken ct)
        => new((await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id),
               (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id));

    public async Task<IReadOnlyList<ResolutionDiagnostic>> ValidateAsync(
        Guid instanceId, SchemaCatalog catalog, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(instanceId, ct)
            ?? throw new NotFoundException($"DocumentInstance {instanceId} not found");

        var diagnostics = new List<ResolutionDiagnostic>();
        var view = DocumentView.From(instance);
        var context = await entityResolver.ResolveAsync(view, ct: ct);
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
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.CompositeTypeId, catalog.DocumentTypes);
        ResolutionScanner.ScanMissingRequired(context, fields, diagnostics);

        // Соответствие значений объявленному типу (issue #461) — предупреждениями: данные накоплены,
        // и половина документов перестала бы выпускаться в тот же день.
        ValueTypeScanner.Scan(context, fields, catalog.DocumentTypes, catalog.Primitives, diagnostics);

        // Материалы без документа качества (issue #585) — предупреждениями, как и при выпуске.
        QualityLinkScanner.Scan(context, instance.CompositeTypeId, catalog.AllTypes, diagnostics);

        return diagnostics;
    }
}
