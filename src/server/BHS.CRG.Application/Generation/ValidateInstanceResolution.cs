using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Прогоняет полный цикл разрешения ссылок для экземпляра (как при генерации),
/// но вместо генерации возвращает собранную диагностику. Используется для проверки
/// «по требованию» из UI.
///
/// Сам прогон живёт в <see cref="IInstanceResolutionValidator"/>: пакетным вызывающим (сверка по
/// комплекту) нужно читать справочники схемы один раз на прогон, а не на документ (issue #628), и
/// через запрос MediatR такое не передашь.
/// </summary>
public record ValidateInstanceResolutionQuery(Guid InstanceId) : IRequest<IReadOnlyList<ResolutionDiagnostic>>;

public class ValidateInstanceResolutionHandler(IInstanceResolutionValidator validator)
    : IRequestHandler<ValidateInstanceResolutionQuery, IReadOnlyList<ResolutionDiagnostic>>
{
    public async Task<IReadOnlyList<ResolutionDiagnostic>> Handle(ValidateInstanceResolutionQuery q, CancellationToken ct)
        => await validator.ValidateAsync(q.InstanceId, await validator.LoadCatalogAsync(ct), ct);
}
