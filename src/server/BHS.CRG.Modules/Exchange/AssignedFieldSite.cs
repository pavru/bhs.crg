namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Контекст узкой полевой подсказки. Work берёт устройство и сотрудника из проверенной активной
/// сессии и проверяет назначение на стройку и дату перед каждым вызовом. Сам DTO ничего не
/// удостоверяет и не является параметром публичного API поставщика.
/// </summary>
public sealed record AssignedFieldSite(Guid DeviceId, Guid EmployeeId, Guid ConstructionId, DateOnly Date);
