using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Результат последней сверки «реестр материалов ↔ карта документов качества» по комплекту
/// (issue #628). Одна строка на комплект, заменяется при каждом прогоне — тот же жизненный цикл, что
/// у <see cref="DocumentSetOutput"/>, и по той же причине: прогон долгий и фоновый, а спросить его
/// итог могут в любой момент и не тем же запросом, который его запускал.
///
/// Хранится ИМЕННО отчёт, а не «состояние комплекта»: он верен на <see cref="CompletedAt"/> и с
/// правкой данных устаревает молча. Поэтому дата отдаётся вместе с ним всегда — «сверка чистая»
/// без даты читалось бы как утверждение о сегодняшнем дне.
/// </summary>
public class QualityAuditRun : Entity
{
    public Guid SetId { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }

    /// <summary>Сколько документов комплекта проверено.</summary>
    public int Documents { get; private set; }

    /// <summary>Документы, которые проверить НЕ удалось (тип удалён, набор не читается).</summary>
    public int Failed { get; private set; }

    public int MaterialsWithoutDoc { get; private set; }
    public int ImplausibleDocs { get; private set; }

    /// <summary>Сколько находок было ВСЕГО. Строк сохранено не больше предела показа, и без полного
    /// числа усечённый список читался бы как весь.</summary>
    public int TotalFindings { get; private set; }

    /// <summary>Находки (усечённые до предела показа) — JSON-массив строк отчёта.</summary>
    public string RowsJson { get; private set; } = "[]";

    private QualityAuditRun() { }

    public static QualityAuditRun Create(Guid setId, int documents, int failed, int materialsWithoutDoc,
        int implausibleDocs, int totalFindings, string rowsJson)
        => new()
        {
            SetId = setId,
            CompletedAt = DateTimeOffset.UtcNow,
            Documents = documents,
            Failed = failed,
            MaterialsWithoutDoc = materialsWithoutDoc,
            ImplausibleDocs = implausibleDocs,
            TotalFindings = totalFindings,
            RowsJson = rowsJson,
        };
}
