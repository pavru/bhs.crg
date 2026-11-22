using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Действия журнала ядра об отчётах (ТЗ CORE-28; change <c>work-14-report-journal</c>) — дубль ключевых действий:
/// удаление черновика, правка после подачи, прогон автоподачи (<c>work-15</c>), приёмка, возврат и правка менеджером
/// (<c>work-16</c>). Свидетельство само по себе лежит в
/// журнале отчёта (одной транзакцией с правкой); журнал ядра пишется после фиксации и сбоем правку не откатывает.
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

    /// <summary>
    /// Прогон автоподачи (ТЗ WORK-41, CORE-28; <c>work-15</c>) — одна запись на прогон с числом поданных, без цели:
    /// каждый поданный отчёт назван в своём журнале записью «подан автоматически».
    /// </summary>
    public static readonly ModuleActivityAction AutoSubmitted =
        new("work.report.auto-submitted", "Отчёты поданы автоматически", WorkModule.ReportRead);

    /// <summary>Приёмка отчёта менеджером (ТЗ WORK-13, <c>work-16</c>).</summary>
    public static readonly ModuleActivityAction Accepted =
        new("work.report.accepted", "Отчёт принят", WorkModule.ReportRead);

    /// <summary>Возврат отчёта на правку с комментарием (ТЗ WORK-13, <c>work-16</c>).</summary>
    public static readonly ModuleActivityAction Returned =
        new("work.report.returned", "Отчёт возвращён на правку", WorkModule.ReportRead);

    /// <summary>Правка менеджером поданного отчёта (ТЗ WORK-14, <c>work-16</c>).</summary>
    public static readonly ModuleActivityAction ManagerEdited =
        new("work.report.manager-edited", "Отчёт изменён менеджером", WorkModule.ReportRead);

    public static readonly ModuleActivityAction Corrected =
        new("work.report.corrected", "Закрытый отчёт исправлен", WorkModule.ReportRead);

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Deleted, EditedAfterSubmission, AutoSubmitted, Accepted, Returned, ManagerEdited, Corrected];

    /// <summary>Цель записи: «Петров П. П. — Стройка А, 16.11.2026, день».</summary>
    public static string Label(string employee, string site, Report report) =>
        $"{employee} — {site}, {report.Date:dd.MM.yyyy}, {(report.Shift == ReportShift.Night ? "ночь" : "день")}";
}
