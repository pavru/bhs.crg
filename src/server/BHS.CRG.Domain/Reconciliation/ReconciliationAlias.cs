using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Reconciliation;

/// <summary>Кто и насколько уверенно утверждает, что две позиции — одно и то же.</summary>
public enum AliasStatus
{
    /// <summary>Предложено (человеком или агентом), но не подтверждено. В сравнении НЕ участвует.</summary>
    Proposed,

    /// <summary>Подтверждено человеком — применяется при свёртке.</summary>
    Confirmed,

    /// <summary>Отклонено. Хранится, чтобы предложение не всплывало снова.</summary>
    Rejected,
}

/// <summary>
/// Алиас позиции (issue #446, фаза Ф2 из #414): утверждение, что два по-разному записанных
/// наименования обозначают одно и то же. В ручном процессе это знание человека — «Hyperline CM-1U-ML»
/// и «Органайзер СвязьСтройДеталь»; без него позиция даёт две находки-сироты.
///
/// Область общая, не привязанная к конкретной сверке: это утверждение о мире, а не свойство одного
/// сравнения. Узнали один раз — применяется везде.
///
/// В сравнении участвуют ТОЛЬКО подтверждённые. Ограничение несущее: неподтверждённый алиас в пути
/// сравнения и есть модель внутри арифметики — риск P1, ради которого Ф1 строился детерминированным.
/// </summary>
public class ReconciliationAlias : Entity
{
    /// <summary>Нормализованный ключ варианта — тот, что заменяем.</summary>
    public string AliasKey { get; private set; } = null!;

    /// <summary>Нормализованный ключ, к которому сводим.</summary>
    public string CanonicalKey { get; private set; } = null!;

    /// <summary>Наименования как их видит человек: голые нормализованные ключи в списке нечитаемы.</summary>
    public string AliasLabel { get; private set; } = null!;
    public string CanonicalLabel { get; private set; } = null!;

    public AliasStatus Status { get; private set; } = AliasStatus.Proposed;
    public string? Note { get; private set; }
    public string? ProposedBy { get; private set; }
    public string? ConfirmedBy { get; private set; }

    private ReconciliationAlias() { }

    public static ReconciliationAlias Propose(
        string aliasKey, string aliasLabel, string canonicalKey, string canonicalLabel,
        string? note, string? proposedBy)
        => new()
        {
            AliasKey = aliasKey, AliasLabel = aliasLabel,
            CanonicalKey = canonicalKey, CanonicalLabel = canonicalLabel,
            Note = note, ProposedBy = proposedBy,
        };

    public void Review(AliasStatus status, string? note, string? confirmedBy)
    {
        Status = status;
        if (note is not null) Note = note;
        ConfirmedBy = confirmedBy;
        TouchUpdatedAt();
    }
}
