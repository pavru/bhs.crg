using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Действия журнала назначений (ТЗ CORE-28; change <c>work-11-assignments</c>). Объявлены и пишутся одним
/// объектом: название на экране журнала берётся из объявления, а не из вызова.
///
/// <para>Права чтения не названо: кто кого куда назначил — не тайна для сотрудника, которому открыт модуль
/// (инвариант #675 — равный допуск внутри компании). Часы и отчёты сюда не попадают.</para>
/// </summary>
public sealed class AssignmentActions : IModuleActivityActions
{
    public static readonly ModuleActivityAction Created = new("work.assignment.created", "Сотрудник назначен на стройку");

    public static readonly ModuleActivityAction Changed = new("work.assignment.changed", "Изменён период назначения");

    public static readonly ModuleActivityAction Removed = new("work.assignment.removed", "Назначение снято");

    public IReadOnlyList<ModuleActivityAction> Actions => [Created, Changed, Removed];

    /// <summary>Цель записи журнала: «Петров П. П. — Стройка А, монтажник».</summary>
    public static string Label(string employee, string site, Data.AssignmentKind kind) =>
        $"{employee} — {site}, {Data.Assignment.KindText(kind)}";
}
