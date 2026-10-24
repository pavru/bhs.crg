using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Разбор конъюнктурного анализа (<c>OpenXML_KA</c>) — ТЗ PLAN-7, PLAN-9.</summary>
public static class ConjunctureReader
{
    public static ParsedConjuncture Read(GrandSmetaFile file)
    {
        file.Expect(GrandSmetaKind.Conjuncture);
        var validation = GrandSmetaValidation.Run(file);
        return GrandSmetaFile.Read(() =>
        {
            var root = file.Root;
            var chapters = root.Chapters().ToList();
            if (chapters.Count == 0)
                throw GrandSmetaFile.Refuse(GrandSmetaRefusal.DocumentInvalid,
                    "В конъюнктурном анализе нет ни одного раздела — разбирать нечего.");

            var suppliers = root.Elements("Counterparties").Elements("Counterparty")
                .Select((c, i) => new ConjunctureSupplier(i + 1, c.Text("Caption"), c.Text("INN"), c.Text("KPP"),
                    c.Text("SupplierKind")))
                .ToList();
            var unparsed = new List<Unparsed>();
            var parsed = new List<ConjunctureChapter>();
            for (var ci = 0; ci < chapters.Count; ci++)
            {
                var chapter = chapters[ci];
                var caption = chapter.Text("Caption") ?? "";
                var positions = new List<ConjuncturePosition>();
                var index = 0;
                foreach (var p in chapter.Elements("Position"))
                {
                    index++;
                    var place = $"раздел {ci + 1} «{caption}», позиция {p.Text("Number")}".TrimEnd();
                    if (validation.Failed(p, out var failed, place))
                    {
                        unparsed.Add(failed);
                        continue;
                    }

                    try
                    {
                        positions.Add(Position(p, index, suppliers));
                    }
                    catch (GrandSmetaFormatException ex)
                    {
                        unparsed.Add(new Unparsed(place, p.Line(), ex.Message));
                    }
                }

                parsed.Add(new ConjunctureChapter(ci + 1, caption, positions));
            }

            var props = root.Element("Properties");
            var dates = root.Element("DocDates");
            var requisites = new ConjunctureRequisites(props?.Text("LocNum"), props?.Text("Description"),
                props?.Text("Constr"), props?.Text("Object"), dates?.CreationDate(), dates?.Date("CurrPriceDate"));
            return new ParsedConjuncture(file.Source, requisites, suppliers, parsed, unparsed);
        });
    }

    private static ConjuncturePosition Position(XElement p, int index, IReadOnlyList<ConjunctureSupplier> suppliers)
    {
        var number = p.Text("Number") ?? throw new GrandSmetaFormatException("у позиции нет номера");
        var offers = p.Elements("Items").Elements("Item").Select(i => Offer(i, suppliers)).ToList();
        var prices = offers.Where(o => o.SellingPrice is not null).Select(o => o.SellingPrice!.Value).ToList();
        return new ConjuncturePosition(index, number, p.Text("Caption") ?? "", p.Text("Units"), p.HasOption("Inactive"),
            offers, prices.Count > 0 ? prices.Min() : null);
    }

    private static ConjunctureOffer Offer(XElement i, IReadOnlyList<ConjunctureSupplier> suppliers)
    {
        ConjunctureSupplier? supplier = null;
        if (i.Number("Supplier") is { } n)
            supplier = suppliers.FirstOrDefault(s => s.Number == n)
                ?? throw new GrandSmetaFormatException($"предложение ссылается на поставщика {n}, а их в списке {suppliers.Count}");
        return new ConjunctureOffer(i.Text("Number") ?? "", i.Text("Caption") ?? "", i.Text("Units"), supplier,
            i.Money("OptPrice"), i.Number("VAT"), i.Money("OptPriceWithVAT"), i.Date("SellingDate"),
            i.Text("ProducerCountry"), i.Text("KSRGroup"), i.Text("UseTransp") == "True");
    }
}
