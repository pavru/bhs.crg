namespace BHS.CRG.Application.Generation;

/// <summary>
/// Подмешивает в элементы массивов (материалы) ссылку на документ качества из общей библиотеки
/// по идентичности материала (артикул/наименование) — поле «ДокументПодтверждающийКачетво».
/// Запускается после инъекции наборов данных и до финального разрешения $ref.
/// </summary>
public interface IQualityLinkResolver
{
    Task InjectAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default);
}
