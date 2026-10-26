using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Работа или материал — вид позиции ЛСР (change <c>plan-31-estimate-ui-carriers</c>, D2; решение владельца
/// 26.10.2026). Правило выведено по всем ЛСР образцов <c>TestData/GrandSmeta</c>:
///
/// <list type="bullet">
///   <item>шифр расценки (<c>ГЭСН…</c>, <c>ФЕР…</c>) — всегда работа, что бы ни стояло рядом;</item>
///   <item>ссылка на конъюнктурный анализ (<c>MarketAnalysisDocLink</c>) — материал: так программа пишет и
///   <c>ТЦ_…</c>, и «Счёт-фактура № …», и позицию сборника с ценой по КА;</item>
///   <item>шифр сборника цен материалов — <c>ФССЦ-</c>, <c>ФСБЦ-</c> (ФСНБ-2022), <c>ТЦ_</c> — материал и без
///   ссылки на КА;</item>
///   <item>остальное — работа: «Вспомогательная позиция», «Контроль гофры», позиция без шифра.</item>
/// </list>
///
/// <para>⚠️ Спорное остаётся работой нарочно (Risks design): лишнее «не сопоставлено» видно на экране, а работа,
/// ошибочно названная материалом, молча пропала бы из плана. Сборники и базы, которых в образцах нет (<c>ТЕР</c>,
/// <c>ТССЦ</c>…), поэтому не перечислены: новый вид позиции — новый образец и строка здесь.</para>
/// </summary>
public static class EstimatePositionKinds
{
    private static readonly string[] Rates = ["ГЭСН", "ФЕР"];

    private static readonly string[] MaterialPrices = ["ФССЦ-", "ФСБЦ-", "ТЦ_"];

    public static EstimateRowKind Of(LocalEstimatePosition position)
    {
        var code = position.Code?.Trim() ?? "";
        if (Rates.Any(r => code.StartsWith(r, StringComparison.OrdinalIgnoreCase))) return EstimateRowKind.Work;
        return position.MarketAnalysis is not null || IsMaterialPrice(code) ? EstimateRowKind.Material : EstimateRowKind.Work;
    }

    /// <summary>
    /// Код материала строки-материала: шифр сборника цен (<c>ФССЦ-</c>, <c>ФСБЦ-</c>, <c>ТЦ_</c> — строка КА) — или
    /// ничего. У позиции по КА в поле шифра бывает и номер документа («Счёт-фактура №68 от 21.08.2026», один на
    /// все его позиции): кодом материала он быть не может — запомненное по коду подставило бы выбор одной
    /// позиции всем остальным, и материал запоминается по наименованию.
    /// </summary>
    public static string? MaterialCode(LocalEstimatePosition position) =>
        position.Code?.Trim() is { Length: > 0 } code && IsMaterialPrice(code) ? code : null;

    private static bool IsMaterialPrice(string code) =>
        MaterialPrices.Any(p => code.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
