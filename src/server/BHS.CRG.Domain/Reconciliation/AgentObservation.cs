using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Reconciliation;

/// <summary>Насколько замечание существенно — по оценке агента, а не системы.</summary>
public enum ObservationSeverity { Info, Warning, Error }

/// <summary>
/// Что человек решил про замечание. Подтверждает и отклоняет ТОЛЬКО человек: агент не подтверждает
/// собственное утверждение — прямое следствие «предложить → подтвердить → персистить» из issue #414.
/// </summary>
public enum ObservationStatus { New, Confirmed, Rejected }

/// <summary>
/// Замечание внешнего ИИ-агента (issue #440) — результат «человеческого» анализа, который до сих пор
/// жил только в переписке и терялся вместе с ней.
///
/// Сознательно ОТДЕЛЬНАЯ сущность от находки сверки. Находка — результат арифметики по спеке
/// (два источника, ключ, оператор), замечание — свободное утверждение, ни к какой спеке не привязанное.
/// Смешать их значило бы пустить модель в путь сравнения и получить «прыгающий» от прогона к прогону
/// отчёт — риск P1, ради которого детерминированное ядро и строилось.
///
/// Отсюда же главное правило представления: замечание НИКОГДА не выдаётся за результат системы.
/// </summary>
public class AgentObservation : Entity
{
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    /// <summary>
    /// Стабильный ключ утверждения, который задаёт сам агент. Тот же урок, что P2 в #414: без него
    /// повторный анализ забил бы журнал дублями, и он перестал бы быть памятью. Повторное сообщение
    /// с тем же ключом в той же области — обновление, а не вторая запись.
    /// </summary>
    public string Key { get; private set; } = null!;

    public string Title { get; private set; } = null!;
    public string? Detail { get; private set; }
    public ObservationSeverity Severity { get; private set; }

    /// <summary>
    /// На что опирается утверждение: документы, источники со строками. Обязательно — замечание без
    /// адреса непроверяемо, а утверждение без опоры это мнение, а не находка.
    /// </summary>
    public JsonDocument References { get; private set; } = null!;

    public ObservationStatus Status { get; private set; } = ObservationStatus.New;

    /// <summary>Кто сообщил — учётная запись, от имени которой работал агент.</summary>
    public string? ReportedBy { get; private set; }

    public string? ReviewedBy { get; private set; }
    public string? ReviewNote { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    private AgentObservation() { }

    public static AgentObservation Create(
        CatalogScope scope, Guid? scopeId, string key, string title, string? detail,
        ObservationSeverity severity, JsonDocument references, string? reportedBy)
        => new()
        {
            Scope = scope, ScopeId = scopeId, Key = key, Title = title, Detail = detail,
            Severity = severity, References = references, ReportedBy = reportedBy,
        };

    /// <summary>
    /// Повторное сообщение того же утверждения. Разбор человека НЕ сбрасывается: агент, прогнав анализ
    /// заново, не должен возвращать в работу то, что уже разобрано, — иначе журнал теряет память
    /// ровно так же, как при нестабильном ключе.
    /// </summary>
    public void Report(string title, string? detail, ObservationSeverity severity,
        JsonDocument references, string? reportedBy)
    {
        Title = title;
        Detail = detail;
        Severity = severity;
        References = references;
        ReportedBy = reportedBy;
        TouchUpdatedAt();
    }

    public void Review(ObservationStatus status, string? note, string? reviewedBy)
    {
        Status = status;
        ReviewNote = note;
        ReviewedBy = reviewedBy;
        ReviewedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }
}
