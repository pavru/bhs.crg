using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.QualityDocs;
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
    Task<IReadOnlyList<ResolutionDiagnostic>> ValidateAsync(
        Guid instanceId, SchemaCatalog catalog, DataAccess access, CancellationToken ct);
}

public class InstanceResolutionValidator(
    IRepository<DomainObject> instanceRepo,
    IGenerationContextBuilder contextBuilder
) : IInstanceResolutionValidator
{
    public Task<SchemaCatalog> LoadCatalogAsync(CancellationToken ct) => contextBuilder.LoadSchemasAsync(ct);

    public async Task<IReadOnlyList<ResolutionDiagnostic>> ValidateAsync(
        Guid instanceId, SchemaCatalog catalog, DataAccess access, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(instanceId, ct)
            ?? throw new NotFoundException($"DocumentInstance {instanceId} not found");

        // Тот же контекст и те же проверки, что у генерации (D1): обе корзины — проверка называет всё,
        // на чём выпуск остановится или о чём предупредит.
        var built = await contextBuilder.BuildAsync(DocumentView.From(instance), access, catalog, ct);
        return built.All;
    }
}
