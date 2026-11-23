using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Data;
using static BHS.CRG.Modules.Tables.ModuleTableColumnKind;

namespace BHS.CRG.Modules.Work.Tables;

internal static class WorkTables
{
    internal const string Date = "Дата", Shift = "Смена", Employee = "Сотрудник", Site = "Стройка";
    internal const string Status = "Состояние", Hours = "Часы", SubmissionTime = "МоментПодачи";
    internal const string LineCount = "ЧислоСтрок", EnteredBy = "АвторВвода", Note = "Примечание";
    internal const string Section = "Раздел", WorkType = "ВидРаботы", Variant = "Вариант", Volume = "Объём";
    internal const string Unit = "Единица", Location = "Место", IsCrew = "Бригада";
    internal const string NotAssigned = "not-assigned";
    internal const string OwnAndSites = "own-and-sites";

    internal static readonly IReadOnlyDictionary<ReportShift, string> Shifts = new Dictionary<ReportShift, string>
    {
        [ReportShift.Day] = "День",
        [ReportShift.Night] = "☾ Ночь",
    };

    internal static readonly IReadOnlyDictionary<ReportStatus, string> Statuses = new Dictionary<ReportStatus, string>
    {
        [ReportStatus.Draft] = "Черновик",
        [ReportStatus.Submitted] = "Подан",
        [ReportStatus.Accepted] = "Принят",
        [ReportStatus.Returned] = "Возвращён",
    };

    private static IReadOnlyList<ModuleTableBoundary> Boundaries(string draftNotice) =>
    [
        new("own", "Ваши отчёты"),
        new("sites", $"Отчёты ваших строек ({ModuleTableBoundary.SitesPlace}). {draftNotice}"),
        new(OwnAndSites, $"Ваши отчёты и отчёты ваших строек ({ModuleTableBoundary.SitesPlace}). {draftNotice}"),
        new("all", $"Все отчёты. {draftNotice}"),
        new(NotAssigned, "Отчёты ваших строек (0). Вы не назначены ни на одну стройку"),
    ];

    private static readonly IReadOnlyList<ModuleTableColumn> ReportHeader =
    [
        new(Date, "Дата", ModuleTableColumnKind.Date),
        new(Shift, "Смена", Choice, Options: [.. Shifts.Values]),
        new(Employee, "Сотрудник", Text),
        new(Site, "Стройка", Text, Sites: true),
    ];

    private static readonly ModuleTableColumn StatusColumn = new(Status, "Статус", Choice, Options: [.. Statuses.Values]);

    private static readonly IReadOnlyList<ModuleTableColumn> LineColumns =
    [
        new(Section, "Раздел", Text),
        new(WorkType, "Вид работ", Text),
        new(Variant, "Вариант", Text),
        new(Volume, "Объём", Number),
        new(Unit, "Ед. изм.", Text),
        new(Location, "Место работ", Text),
        new(IsCrew, "Бригадная работа", ModuleTableColumnKind.Boolean),
    ];

    internal static readonly ModuleTable Reports = new(
        "reports", "Отчёты монтажников", "отчёт", WorkModule.ModuleCode, ModuleTableIsolation.PerUser,
        Boundaries("Черновики других сотрудников видны без содержимого до подачи"),
        [
            .. ReportHeader, StatusColumn,
            new(Hours, "Часы", Number),
            new(SubmissionTime, "Подача или автоподача", Text),
            new(LineCount, "Число строк", Number),
            new(EnteredBy, "Автор ввода", Text),
        ],
        typeof(ReportTableRows), WorkRecordTypes.ReportCode,
        Breakdown: new("Строки работы", [.. LineColumns.Select(c => new TableBreakdownColumn(
            c.Key, c.Title, c.Key == IsCrew ? Text : c.Kind, Follows: c.Key == Volume ? LineCount : null))]),
        MissingRowIsNotFound: true);

    internal static readonly ModuleTable Lines = new(
        "lines", "Строки работы", "строка работы", WorkModule.ModuleCode, ModuleTableIsolation.PerUser,
        Boundaries("Строки чужих черновиков появляются после подачи"),
        [.. ReportHeader, .. LineColumns, StatusColumn], typeof(LineTableRows), MissingRowIsNotFound: true);
}
