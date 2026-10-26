using System.Globalization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Строки будущей редакции из разобранной ЛСР (change <c>plan-07-estimate-import</c>): одна позиция файла —
/// одна строка, в порядке файла.
///
/// <para><b>Неактивная позиция строкой не становится</b> (D3 <c>plan-31</c>, решение владельца 26.10.2026): в
/// сумму сметы она не входит, в печатной форме её нет, и в плане ей не место. Отчёт импорта называет её
/// пропущенной (<see cref="Skipped" />) — отдельно от не разобранных.</para>
///
/// <para><b>Строка-материал</b> (D2 <c>plan-31</c>, <see cref="EstimatePositionKinds" />) несёт свой материал:
/// ресурсов у такой позиции программа не пишет, и без него сопоставлять с номенклатурой было бы нечего.</para>
///
/// <para>Сметное наполнение (<c>data</c>) здесь не заполняется: полей у типа «Позиция сметы» модуль не
/// объявляет, схему ведёт администратор (<c>PlanRecordTypes</c>).</para>
/// </summary>
internal static class EstimateImportRows
{
    public static List<EstimateRowDraft> Drafts(ParsedLocalEstimate model) =>
    [
        .. model.Chapters.SelectMany(chapter => chapter.Positions.Where(p => !p.IsInactive).Select(p =>
        {
            var kind = EstimatePositionKinds.Of(p);
            // Без наименования строка не записывается — в файле его заменяет шифр, а нет и шифра — номер.
            var name = string.IsNullOrWhiteSpace(p.Caption) ? p.Code ?? $"Позиция {p.Number}" : p.Caption;
            List<EstimateMaterialDraft> materials = [.. p.Materials.Select(m => new EstimateMaterialDraft(m.Caption, m.Quantity, m.Code, m.Units))];
            // Код материала — только шифр сборника цен: «Счёт-фактура № …» у позиций по КА — номер документа, один
            // на все его позиции, и запомненное по нему подставило бы выбор одной позиции всем остальным.
            if (kind == EstimateRowKind.Material && materials.Count == 0)
                materials.Add(new EstimateMaterialDraft(name, p.Quantity, EstimatePositionKinds.MaterialCode(p), p.Units));
            return new EstimateRowDraft(
                name,
                LsrSectionCode: chapter.Index.ToString(CultureInfo.InvariantCulture),
                LsrSectionName: chapter.Caption,
                // Порядковый номер в разделе, а не номер файла («13», «1.1»): тот сквозной по документу, и одна
                // позиция, добавленная в начало, сдвинула бы его во всех разделах ниже. Номер в разделе — часть
                // ключа переноса распределения при повторном импорте (D9 зонтика); номер файла — только показ.
                NumberInSection: p.IndexInChapter.ToString(CultureInfo.InvariantCulture),
                RateCode: p.Code,
                SourceVolume: p.Quantity,
                SourceUnit: p.Units,
                UnitCost: p.UnitPrice,
                Total: p.Total,
                Materials: materials,
                Labor: p.Labor is { } labor ? [labor] : [],
                Number: p.Number,
                Subordinate: p.IsSubordinate,
                Kind: kind);
        })),
    ];

    /// <summary>Позиции, которые строками не стали, — с местом в файле: неактивные (D3 <c>plan-31</c>).</summary>
    public static List<SkippedPosition> Skipped(ParsedLocalEstimate model) =>
    [
        .. model.Chapters.SelectMany(chapter => chapter.Positions.Where(p => p.IsInactive).Select(p =>
            new SkippedPosition(LocalEstimateReader.Place(chapter.Index, chapter.Caption, p.Number, p.Code),
                SkippedPosition.Inactive, "неактивна в файле — пропущена"))),
    ];
}

/// <summary>Позиция файла, которая строкой не стала, — где и почему (D3 <c>plan-31</c>).</summary>
/// <param name="Reason"><c>inactive</c> — неактивна в файле.</param>
public sealed record SkippedPosition(string Place, string Reason, string Text)
{
    public const string Inactive = "inactive";
}
