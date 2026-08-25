using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Строка плана комплекта: «документов такого-то типа должно быть столько-то» (issue #796).
///
/// План задаётся ТОЛЬКО на комплекте. Планы раздела и стройки не хранятся — они считаются
/// консолидацией нижележащих: хранить их значило бы завести две записи об одном и том же и ждать,
/// когда они разойдутся.
///
/// Фича строго опциональна: нет строк — нет плана, и процент готовности нигде не показывается.
/// «Ноль процентов» и «плана нет» — разные вещи, и рисовать второе как первое нельзя.
/// </summary>
public class DocumentSetPlanItem : Entity
{
    public Guid DocumentSetId { get; private set; }
    public Guid DocumentTypeId { get; private set; }

    /// <summary>Сколько документов этого типа планируется. Всегда ≥ 1: ноль — это отсутствие строки.</summary>
    public int PlannedCount { get; private set; }

    private DocumentSetPlanItem() { }

    public static DocumentSetPlanItem Create(Guid documentSetId, Guid documentTypeId, int plannedCount)
    {
        // Отказ ПОЛЬЗОВАТЕЛЮ, а не внутренний инвариант: значение приходит из формы плана, и текст
        // должен доехать до экрана. ArgumentOutOfRangeException превратился бы в «внутреннюю ошибку
        // сервера» — это ловит DomainExceptionPolicyTests.
        if (plannedCount < 1)
            throw new InvalidRequestException(
                "Планируемое количество — от 1. Ноль означает отсутствие строки плана, а не строку с нулём.");

        return new()
        {
            DocumentSetId = documentSetId,
            DocumentTypeId = documentTypeId,
            PlannedCount = plannedCount,
        };
    }

    /// <summary>Восстановление из резервной копии (issue #833).</summary>
    public static DocumentSetPlanItem Restore(Guid id, Guid documentSetId, Guid documentTypeId, int plannedCount,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, DocumentSetId = documentSetId, DocumentTypeId = documentTypeId,
            PlannedCount = plannedCount, CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
