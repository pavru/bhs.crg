namespace BHS.CRG.Modules.Ports;

/// <summary>Событие, сохранённое модулем вместе с действием для доставки после фиксации.</summary>
public sealed record ModuleDeferredActivity(
    Guid EventId, Guid? AuthorId, string AuthorName, DateTimeOffset OccurredAt,
    string TargetId, string TargetLabel, string After);

/// <summary>Доставка записанного события с исходным автором и временем. Повтор того же ID безопасен.</summary>
public interface IModuleDeferredActivityLog
{
    Task PublishAsync(ModuleActivityAction action, ModuleDeferredActivity saved, CancellationToken ct = default);
}
