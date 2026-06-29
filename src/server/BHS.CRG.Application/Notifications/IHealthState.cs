namespace BHS.CRG.Application.Notifications;

/// <summary>Состояние одного отслеживаемого компонента (БД, хранилище, Ollama).</summary>
public record ComponentHealth(string Name, bool Healthy, string? Detail, DateTimeOffset CheckedAt);

/// <summary>Текущий снимок состояния системы и внешних компонент (обновляется фоновой проверкой).</summary>
public interface IHealthState
{
    IReadOnlyList<ComponentHealth> Snapshot { get; }
}
