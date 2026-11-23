using BHS.CRG.Modules.Printing;

namespace BHS.CRG.Modules.Plan.Printing;

internal static class OfferPrintTemplate
{
    internal static readonly ModulePrintTemplate Default = new("Коммерческое предложение", """
        #let offer = json("data.json")
        #let value(x) = if x == none { [—] } else { str(x) }
        #let money(x) = if x == none { [—] } else {
          let parts = str(calc.round(x, digits: 2)).split(".")
          let fraction = parts.at(1, default: "")
          parts.first() + "," + fraction + "0" * (2 - fraction.len())
        }
        #let date(x) = if x == none { [—] } else { x.split("-").rev().join(".") }
        #set page(paper: "a4", margin: 18mm, numbering: "1")
        #set text(lang: "ru", size: 10pt)

        = Коммерческое предложение № #offer.number
        От #date(offer.date). Версия #offer.version от #date(offer.versionDate).

        #if offer.construction != none [*Стройка:* #offer.construction\ ]
        #if offer.customer != none [*Заказчик:* #offer.customer]

        #table(
          columns: (auto, 1fr, auto, auto, auto, auto),
          align: (left, left, right, left, right, right),
          inset: 5pt,
          table.header([*№*], [*Наименование*], [*Количество*], [*Ед.*], [*Цена*], [*Сумма*]),
          ..offer.lines.enumerate().map(((index, line)) => (
            value(if line.number == none { index + 1 } else { line.number }),
            value(line.name), value(line.volume), value(line.unit), money(line.price), money(line.amount),
          )).flatten(),
        )

        #align(right)[
          Сумма строк #money(offer.money.linesTotal) \
          #if offer.vat.mode == "none" [Без НДС \ ] else [
            #if offer.vat.mode == "included" [В том числе НДС] else [НДС сверху]
            #value(offer.vat.rate)% #money(offer.money.vatAmount) \
          ]
          *Итого к оплате #money(offer.money.total)*
        ]

        #for (field, text) in offer.conditions.pairs() {
          if type(text) == str and text != "" { [*#field:* #text]; parbreak() }
        }
        """);
}
