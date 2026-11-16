using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Действие не годится для текущего статуса отчёта (ТЗ WORK-9, WORK-13; change <c>work-16-review</c>): принять или
/// вернуть можно только поданный. Адрес отвечает 409 <c>{ code: "report-status", status, error }</c> — кодом причины он
/// отличается от конфликта версии: «обновите страницу» здесь ничего не изменит.
/// </summary>
public sealed class ReportStatusException(ReportStatus status) : ConflictException(
    $"Отчёт в статусе «{Report.StatusName(status)}»: принять или вернуть можно только поданный отчёт.")
{
    public const string ReasonCode = "report-status";

    public ReportStatus Status => status;
}
