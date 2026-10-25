using System.Globalization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Строки будущей редакции из разобранной ЛСР (change <c>plan-07-estimate-import</c>): одна позиция файла —
/// одна строка, в порядке файла. Неактивные позиции тоже ложатся строками — сводка отчёта называет их среди
/// строк, а итог у них нулевой, как его записала программа.
///
/// <para>Сметное наполнение (<c>data</c>) здесь не заполняется: полей у типа «Позиция сметы» модуль не
/// объявляет, схему ведёт администратор (<c>PlanRecordTypes</c>).</para>
/// </summary>
internal static class EstimateImportRows
{
    public static List<EstimateRowDraft> Drafts(ParsedLocalEstimate model) =>
    [
        .. model.Chapters.SelectMany(chapter => chapter.Positions.Select(p => new EstimateRowDraft(
            // Без наименования строка не записывается — в файле его заменяет шифр, а нет и шифра — номер.
            string.IsNullOrWhiteSpace(p.Caption) ? p.Code ?? $"Позиция {p.Number}" : p.Caption,
            LsrSectionCode: chapter.Index.ToString(CultureInfo.InvariantCulture),
            LsrSectionName: chapter.Caption,
            NumberInSection: p.Number,
            RateCode: p.Code,
            SourceVolume: p.Quantity,
            SourceUnit: p.Units,
            UnitCost: p.UnitPrice,
            Total: p.Total,
            Materials: [.. p.Materials.Select(m => new EstimateMaterialDraft(m.Caption, m.Quantity, m.Code, m.Units))],
            Labor: p.Labor is { } labor ? [labor] : []))),
    ];
}
