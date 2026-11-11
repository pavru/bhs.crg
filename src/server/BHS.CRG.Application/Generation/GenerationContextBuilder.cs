using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Контекст генерации и его диагностика — в двух корзинах (ТЗ CORE-21; change
/// core-generation-input-one-path, D1).
/// </summary>
/// <param name="Resolution">Диагностика шагов разрешения и висящих ссылок: «контекст не собрался».</param>
/// <param name="Checks">Полнота обязательных, типы значений, документы качества: «собрался, но выпускать
/// нельзя» (ошибки) или «есть о чём сказать» (предупреждения).</param>
public sealed record BuiltGenerationContext(
    GenerationContext Context,
    IReadOnlyList<ResolutionDiagnostic> Resolution,
    IReadOnlyList<ResolutionDiagnostic> Checks)
{
    /// <summary>Обе корзины подряд — в том порядке, в каком их возвращала проверка документа.</summary>
    public IReadOnlyList<ResolutionDiagnostic> All => [.. Resolution, .. Checks];
}

/// <summary>
/// Сборка контекста генерации — ОДНА на генерацию, предпросмотр, проверку документа и отладочный
/// пакет. Прежде семь шагов были написаны четырежды, и копии уже расходились (пакет, например, не
/// собирал диагностику наборов данных вовсе).
///
/// <para>Что останавливает работу, решает вызывающий — выбором корзины, а не фильтром по кодам:
/// генерация отказывает на ошибке в любой, предпросмотр — только в <c>Resolution</c> (иначе он гас бы
/// на каждом незаполненном поле), проверка документа возвращает обе, пакет перечисляет обе в README.</para>
/// </summary>
public interface IGenerationContextBuilder
{
    /// <summary>Справочники схемы — один раз на прогон (см. <see cref="SchemaCatalog" />).</summary>
    Task<SchemaCatalog> LoadSchemasAsync(CancellationToken ct);

    Task<BuiltGenerationContext> BuildAsync(
        DocumentView view, DataAccess access, SchemaCatalog schemas, CancellationToken ct);
}

public class GenerationContextBuilder(
    IRepository<DocumentType> docTypeRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver
) : IGenerationContextBuilder
{
    public async Task<SchemaCatalog> LoadSchemasAsync(CancellationToken ct)
        => new((await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id),
               (await primitiveRepo.GetAllAsync(ct)).ToDictionary(t => t.Id));

    public async Task<BuiltGenerationContext> BuildAsync(
        DocumentView view, DataAccess access, SchemaCatalog schemas, CancellationToken ct)
    {
        var resolution = new List<ResolutionDiagnostic>();
        var context = await entityResolver.ResolveAsync(view, ct: ct);
        await dataSetResolver.InjectAsync(context, view, access, resolution, ct);
        // Значения по умолчанию из схемы типа (issue #53) — для полей, оставшихся без значения
        // после реквизитов и привязок (самый низкий приоритет).
        await entityResolver.ApplyDefaultsAsync(context, view, ct);
        // Enum-поля: код → отображаемое имя (issue #59) — иначе в PDF попадёт сырой код.
        await entityResolver.ResolveEnumLabelsAsync(context, view, ct);
        // Документы качества по идентичности материала (issue #585): без этого шага проверка не увидела
        // бы ни привязанных сертификатов, ни их отсутствия.
        await qualityLinkResolver.InjectAsync(context, view, ct);
        // Наборы данных могли добавить ссылки на каталог ($ref) — второй проход (идемпотентно).
        await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
        // Расчётные поля (issue #368): ПОСЛЕ финализации входов, ДО проверок полноты. Цикл — ошибка,
        // ошибка формулы — предупреждение.
        await entityResolver.ResolveComputedFieldsAsync(context, view, resolution, ct);
        // Оставшиеся $ref — ошибки: ссылка на удалённую запись не разрешилась.
        ResolutionScanner.ScanLeftoverRefs(context, resolution);

        var checks = new List<ResolutionDiagnostic>();
        // Полнота обязательных (issue #296, фаза 0b) — ПОСЛЕ полного резолва: обязательность —
        // инвариант генерации, а не сохранения.
        var fields = DocumentTypeSchemaReader.EffectiveFields(view.DocumentTypeId, schemas.DocumentTypes);
        ResolutionScanner.ScanMissingRequired(context, fields, checks);
        // Соответствие значений объявленному типу (issue #461) — предупреждениями: данные накоплены, и
        // половина документов перестала бы выпускаться в тот же день.
        ValueTypeScanner.Scan(context, fields, schemas.DocumentTypes, schemas.Primitives, checks);
        // Материалы без документа качества (issue #585) — предупреждениями: выпуск не блокируем, но и
        // молчать нельзя.
        QualityLinkScanner.Scan(context, view.DocumentTypeId, schemas.AllTypes, checks);

        return new BuiltGenerationContext(context, resolution, checks);
    }
}
