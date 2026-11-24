using BHS.CRG.Modules.Printing;

namespace BHS.CRG.Modules.Plan.Printing;

internal static class ScheduleWorksPrintTemplate
{
    internal static readonly ModulePrintTemplate Default = new("Список работ графика", """
        #let schedule = json("data.json")
        #let value(x) = if x == none { [—] } else { str(x) }
        #let date(x) = if x == none { [—] } else { x.split("-").rev().join(".") }
        #let volume(row) = if row.volume == none { [—] } else {
          let amount = str(row.volume).replace(".", ",")
          if row.unit == none { amount } else { [#amount #row.unit] }
        }
        #set page(paper: "a4", flipped: true, margin: 17mm, numbering: "1")
        #set text(lang: "ru", size: 10pt)

        = График стройки
        *#schedule.name* #linebreak()
        #if schedule.construction != none [#schedule.construction #linebreak()]
        #schedule.source

        #if schedule.works.len() == 0 [
          В графике пока нет работ.
        ] else [
          #table(
            columns: (auto, 2fr, 1fr, auto, auto, auto, auto, 1.3fr),
            align: (right, left, left, center, center, right, right, left),
            inset: 5pt,
            table.header([*№*], [*Работа*], [*Раздел*], [*Начало*], [*Окончание*], [*Раб. дни*], [*Объём*], [*Исполнители*]),
            ..schedule.works.enumerate().map(((index, row)) => (
              value(index + 1),
              [#row.name#if row.warnings.len() > 0 [#linebreak() #text(size: 8pt, row.warnings.join(" "))]],
              value(row.section),
              date(row.start),
              date(row.end),
              value(row.durationWorkingDays),
              volume(row),
              if row.assignees.len() > 0 { row.assignees.join(", ") } else { [—] },
            )).flatten(),
          )
        ]
        """);
}
