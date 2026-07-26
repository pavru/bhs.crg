using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Reconciliation;

/// <summary>Чем закончился прогон сверки.</summary>
public enum ReconciliationRunStatus { Running, Completed, Failed }

/// <summary>
/// Что показала находка. «Устранено» здесь СОЗНАТЕЛЬНО отсутствует: оно вычисляется из истории
/// прогонов (было расхождение — стало совпадение), а хранимое поле пришлось бы поддерживать в
/// согласии с историей вручную и рано или поздно разошлось бы с ней.
/// </summary>
public enum FindingStatus { Match, Mismatch, MissingLeft, MissingRight }

/// <summary>Что человек решил про находку.</summary>
public enum DecisionKind
{
    /// <summary>Расхождение признано нормой (давальческое оборудование, «Учтено в ЭОМ»).</summary>
    Accepted,

    /// <summary>Позиция исключена из сверки как неприменимая.</summary>
    Suppressed,
}

/// <summary>
/// Определение сверки: что с чем сопоставлять. Живёт на уровне комплекта/раздела/стройки.
/// </summary>
public class ReconciliationDefinition : Entity
{
    public string Name { get; private set; } = null!;
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    /// <summary>Спека сравнения: стороны, ключевые колонки, колонка значения, оператор и допуск.
    /// Данными, а не кодом, — чтобы новый раздел не означал новый C# (решение #414).</summary>
    public JsonDocument Spec { get; private set; } = null!;

    private ReconciliationDefinition() { }

    public static ReconciliationDefinition Create(string name, CatalogScope scope, Guid? scopeId, JsonDocument spec)
        => new() { Name = name, Scope = scope, ScopeId = scopeId, Spec = spec };

    public void Update(string name, JsonDocument spec)
    {
        Name = name;
        Spec = spec;
        TouchUpdatedAt();
    }
}

/// <summary>Один прогон сверки — снимок на момент времени; история прогонов и даёт «Устранено».</summary>
public class ReconciliationRun : Entity
{
    public Guid DefinitionId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; private set; }
    public ReconciliationRunStatus Status { get; private set; } = ReconciliationRunStatus.Running;
    public string? Error { get; private set; }

    public int MatchCount { get; private set; }
    public int MismatchCount { get; private set; }
    public int MissingLeftCount { get; private set; }
    public int MissingRightCount { get; private set; }

    private ReconciliationRun() { }

    public static ReconciliationRun Start(Guid definitionId) => new() { DefinitionId = definitionId };

    public void Complete(int match, int mismatch, int missingLeft, int missingRight)
    {
        MatchCount = match;
        MismatchCount = mismatch;
        MissingLeftCount = missingLeft;
        MissingRightCount = missingRight;
        Status = ReconciliationRunStatus.Completed;
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }

    public void Fail(string error)
    {
        Error = error;
        Status = ReconciliationRunStatus.Failed;
        FinishedAt = DateTimeOffset.UtcNow;
        TouchUpdatedAt();
    }
}

/// <summary>
/// Находка одного прогона. Принадлежит прогону — это снимок; переживающее прогоны человеческое
/// решение лежит отдельно, в <see cref="ReconciliationDecision"/>.
/// </summary>
public class ReconciliationFinding : Entity
{
    public Guid RunId { get; private set; }

    /// <summary>Доменный ключ (нормализованные марка/сечение), НЕ порядковый номер строки.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Как позиция называется в документах — для показа человеку; сопоставление идёт по Key.</summary>
    public string Label { get; private set; } = null!;

    public double? LeftValue { get; private set; }
    public double? RightValue { get; private set; }
    public FindingStatus Status { get; private set; }

    /// <summary>Файл, источник, номера строк и колонка по каждой стороне. До ячейки не дотягиваем
    /// намеренно (P3 в #414): строки из PDF приходят от зрительной модели без якоря на ячейку.</summary>
    public JsonDocument Provenance { get; private set; } = null!;

    private ReconciliationFinding() { }

    public static ReconciliationFinding Create(
        Guid runId, string key, string label, double? left, double? right,
        FindingStatus status, JsonDocument provenance)
        => new()
        {
            RunId = runId, Key = key, Label = label,
            LeftValue = left, RightValue = right, Status = status, Provenance = provenance,
        };
}

/// <summary>
/// Решение человека по позиции. Привязано к ОПРЕДЕЛЕНИЮ и ключу, а не к прогону — центральное решение
/// #414: привяжи его к прогону, и следующий прогон потеряет память о том, что уже разобрано, а вместе
/// с ней и весь смысл журнала.
/// </summary>
public class ReconciliationDecision : Entity
{
    public Guid DefinitionId { get; private set; }
    public string Key { get; private set; } = null!;
    public DecisionKind Kind { get; private set; }
    public string? Note { get; private set; }
    public string? DecidedBy { get; private set; }

    private ReconciliationDecision() { }

    public static ReconciliationDecision Create(Guid definitionId, string key, DecisionKind kind,
        string? note, string? decidedBy)
        => new() { DefinitionId = definitionId, Key = key, Kind = kind, Note = note, DecidedBy = decidedBy };

    public void Update(DecisionKind kind, string? note, string? decidedBy)
    {
        Kind = kind;
        Note = note;
        DecidedBy = decidedBy;
        TouchUpdatedAt();
    }
}
