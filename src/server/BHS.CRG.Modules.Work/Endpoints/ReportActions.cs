using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Действия журнала ядра об отчётах (ТЗ CORE-28; change <c>work-14-report-journal</c>) — дубль ключевых действий:
/// удаление черновика и правка после подачи. Свидетельство само по себе лежит в журнале отчёта (одной транзакцией с
/// правкой); журнал ядра пишется после фиксации и сбоем правку не откатывает.
///
/// <para><b>Право чтения — <c>work.report.read</c></b>: запись называет сотрудника, стройку, дату и смену отчёта любой
/// стройки, а видеть отчёты всех строек — это право «читать отчёты всех строек». Без него хватало бы открытого модуля,
/// и монтажник видел бы в журнале чужие отчёты.</para>
/// </summary>
public sealed class ReportActions : IModuleActivityActions
{
    public static readonly ModuleActivityAction Deleted =
        new("work.report.deleted", "Черновик отчёта удалён", WorkModule.ReportRead);

    public static readonly ModuleActivityAction EditedAfterSubmission =
        new("work.report.edited-after-submission", "Отчёт изменён после подачи", WorkModule.ReportRead);

    public IReadOnlyList<ModuleActivityAction> Actions => [Deleted, EditedAfterSubmission];

    /// <summary>Цель записи: «Петров П. П. — Стройка А, 16.11.2026, день».</summary>
    public static string Label(string employee, string site, Report report) =>
        $"{employee} — {site}, {report.Date:dd.MM.yyyy}, {(report.Shift == ReportShift.Night ? "ночь" : "день")}";
}
