using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Разбор ведомости объёмов работ (<c>OpenXML VOW</c>) — ТЗ PLAN-7.</summary>
public static class VolumeStatementReader
{
    public static ParsedVolumeStatement Read(GrandSmetaFile file)
    {
        file.Expect(GrandSmetaKind.VolumeStatement);
        var validation = GrandSmetaValidation.Run(file);
        return GrandSmetaFile.Read(() =>
        {
            var root = file.Root;
            var chapters = root.Chapters().ToList();
            if (chapters.Count == 0)
                throw GrandSmetaFile.Refuse(GrandSmetaRefusal.DocumentInvalid,
                    "В ведомости объёмов работ нет ни одного раздела — разбирать нечего.");

            var unparsed = new List<Unparsed>();
            var parsed = new List<VolumeChapter>();
            for (var ci = 0; ci < chapters.Count; ci++)
            {
                var chapter = chapters[ci];
                var caption = chapter.Text("Caption") ?? "";
                var notes = new List<string>();
                var works = new List<VolumeWork>();
                string? header = null;
                var index = 0;
                foreach (var e in chapter.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "Header":
                            header = e.Text("Caption");
                            if (header is not null) notes.Add(header);
                            continue;
                        case "Comment":
                            if (e.Text("Caption") is { } note) notes.Add(note);
                            continue;
                        case not "Position":
                            continue;
                    }

                    index++;
                    var place = $"раздел {ci + 1} «{caption}», позиция {e.Text("Number")}".TrimEnd();
                    validation.Parse(e, place, works, unparsed, () => Work(e, index, header));
                }

                parsed.Add(new VolumeChapter(ci + 1, caption, chapter.Text("CustomNumber"), notes, works));
            }

            var props = root.Element("Properties");
            var requisites = new VolumeStatementRequisites(
                props?.Text("LocNum"), props?.Text("Description"), props?.Text("Constr"), props?.Text("Object"),
                props?.Text("Stage"), props?.Text("Comment"), root.Element("DocDates")?.CreationDate());

            return new ParsedVolumeStatement(file.Source, requisites, parsed,
                root.Elements("Variables").Elements("Variable")
                    .Select(v => new VolumeVariable(v.Text("Identifier"), v.Text("Caption"), v.Text("Value"))).ToList(),
                root.Elements("Hyperlinks").Elements("Item")
                    .Select(h => new VolumeHyperlink(h.Text("Caption"), h.Text("URL"))).ToList(),
                root.Elements("Attachments").Elements("Binary")
                    .Select(b => new VolumeAttachment(b.Text("Guid"), b.Text("Filename"),
                        b.Number("Size") is { } size ? (long)size : null)).ToList(),
                unparsed);
        });
    }

    private static VolumeWork Work(XElement p, int index, string? header)
    {
        var number = p.Text("Number") ?? throw new GrandSmetaFormatException("у работы нет номера");
        // Нулевой объём программа не пишет: ни Result, а у пустой строки ведомости — и самого Quantity
        // (ВОР «Техническое здание №1», 63: <Position Number="63" SysID="63" />).
        var q = p.Element("Quantity");
        var quantity = q?.Number("Result") ?? 0m;
        var rows = p.Elements("Quantity").Elements("CalcRows").Elements("Item")
            .Select(r => new VolumeCalcRow(r.Text("Identifier"), r.Text("Caption"), r.Text("Units"), r.Text("Value"),
                r.Text("TotalResult") == "Yes"))
            .ToList();
        var sheets = p.Elements("SupportingDocs").Elements("Item").Concat(p.Elements("SupportingDoc"))
            .Select(s => new SheetReference(s.Text("Caption") ?? FileNameOf(s.Text("URL")), s.Text("URL"),
                s.Text("PageNumber"), s.Text("Comment")))
            .ToList();
        return new VolumeWork(index, number, p.Text("Identifier"), p.Text("Caption") ?? "", p.Text("Code"),
            p.Text("Units"), quantity, q?.Text("Fx"), rows, p.Text("UserComment1"), p.Text("UserComment2"), sheets,
            header, p.Text("SlaveRow") == "Yes");
    }

    /// <summary>Имя файла по адресу ссылки: «grand://this/%24%7BAttachments%7D/Акт осмотра .pdf» → «Акт осмотра
    /// .pdf», «Том 5.6 276-25-ИОС6-03.pdf» → оно само.</summary>
    private static string? FileNameOf(string? url)
    {
        if (url is null) return null;
        var path = Uri.UnescapeDataString(url);
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }
}
