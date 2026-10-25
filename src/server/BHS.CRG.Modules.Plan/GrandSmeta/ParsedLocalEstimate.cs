namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Метод расчёта ЛСР — по <c>Indexes</c> и <c>Parameters</c> (ТЗ PLAN-8). Не опознан — «не
/// определён»: итог из файла читается и так, метод нужен досчёту сметы без итогов и показу.</summary>
public enum CalculationMethod
{
    Unknown,

    /// <summary>Базисно-индексный: базисные цены по статьям × индексы к позиции (<c>IndexesMode</c> ≠ <c>None</c>).</summary>
    Bim,

    /// <summary>Ресурсно-индексный: текущие цены ресурсов, недостающие — базисная × индекс ГОСР.</summary>
    Rim,

    /// <summary>Ресурсный: текущие цены ресурсов без индексов.</summary>
    Rm,
}

/// <summary>Кто посчитал итог позиции: программа (записан в файле) или система (досчёт файла без итогов,
/// <c>GrandSmeta/Totals</c>). Записанный программой итог не пересчитывается никогда (PLAN-8).</summary>
public enum TotalSource
{
    Program,
    ComputedBySystem,
}

/// <summary>Разобранный локальный сметный расчёт (ТЗ PLAN-7, PLAN-8). Неизменяемый; запись в модель сметы —
/// <c>plan-07</c>.</summary>
public sealed record ParsedLocalEstimate(
    GrandSmetaSource Source,
    LocalEstimateRequisites Requisites,
    CalculationMethod Method,
    IReadOnlyList<LocalEstimateChapter> Chapters,
    LocalEstimateTotals Totals,
    IReadOnlyList<Unparsed> Unparsed)
{
    public IEnumerable<LocalEstimatePosition> Positions => Chapters.SelectMany(c => c.Positions);

    /// <summary>Позиций в файле — разобранные и не разобранные: «280 из 300».</summary>
    public int PositionsInFile => Chapters.Sum(c => c.Positions.Count) + Unparsed.Count;
}

/// <summary>Реквизиты расчёта. <see cref="CurrentPriceDate" /> — дата текущего уровня цен
/// (<c>DocDates/@CurrPriceDate</c>), <see cref="BasePrices" /> — базисный уровень («2001»).</summary>
public sealed record LocalEstimateRequisites(
    string? Number,
    string? Name,
    string? Construction,
    string? Object,
    string? Stage,
    DateOnly? CreationDate,
    DateOnly? CurrentPriceDate,
    string? BasePrices);

/// <summary>Итоги документа, как их записала программа. <see cref="Total" /> — сумма итогов разделов (без
/// лимитированных затрат); <see cref="EstimateTotal" /> — «ВСЕГО по смете» с ними (непредвиденные, НДС…):
/// сами лимитированные затраты в модель не входят. <see cref="Labor" /> — затраты труда рабочих, чел.-ч.
/// У досчитанной сметы итог и затраты труда — суммы разделов, а «ВСЕГО по смете» нет: лимитированные затраты
/// система не досчитывает.</summary>
public sealed record LocalEstimateTotals(decimal? Total, decimal? EstimateTotal, decimal? Labor);

/// <summary>Раздел. <see cref="Total" /> — итог раздела в текущих ценах, записанный программой; у досчитанной
/// сметы — сумма активных позиций, а не разобрана хоть одна — null.</summary>
public sealed record LocalEstimateChapter(
    int Index,
    string Caption,
    decimal? Total,
    decimal? Labor,
    IReadOnlyList<LocalEstimatePosition> Positions);

/// <summary>
/// Позиция расчёта (решения владельца 24.10.2026, design plan-05).
///
/// <para><see cref="Number" /> — номер, как в файле («13», подчинённая строка — «1.1»), сквозной по документу;
/// <see cref="IndexInChapter" /> — порядковый номер в разделе (ключ переноса распределения, D9 этапа).</para>
///
/// <para><see cref="Total" /> — «Всего по позиции» в текущих ценах, с НР и СП (<c>TotalWithNP/@TotalCurr</c>),
/// база наценки КП. <see cref="UnitPrice" /> — итог ÷ объём, <b>расчётная</b>: при БИМ с индексами текущей цены
/// единицы программа не пишет, общее у трёх методов — итог позиции.</para>
///
/// <para>Неактивная позиция (<see cref="IsInactive" />) читается с пометкой: в сумму сметы не входит, в печатной
/// форме её нет. Итог программа пишет у неё нулевым (статьи — 0), затраты труда — посчитанными (ЛСР ОС,СКУД, 13).</para>
/// </summary>
public sealed record LocalEstimatePosition(
    int IndexInChapter,
    string Number,
    string? Code,
    string Caption,
    string? Units,
    decimal Quantity,
    decimal Total,
    decimal? TotalBase,
    TotalSource TotalSource,
    decimal? UnitPrice,
    bool IsInactive,
    bool IsSubordinate,
    decimal? Labor,
    IReadOnlyList<PositionMaterial> Materials,
    IReadOnlyList<PositionCoefficient> Coefficients,
    MarketAnalysisLine? MarketAnalysis);

/// <summary>Материал позиции: количество «всего» на позицию — норма × коэффициент к расходу × объём.
/// Удалённый сметчиком ресурс (<c>Attribs="Deleted"</c>) в перечень не входит.</summary>
public sealed record PositionMaterial(string? Code, string Caption, string? Units, decimal Quantity);

/// <summary>Коэффициент позиции — как его назвала программа, со значениями в названии («ОЗП=1,2; ЭМ=1,2…»);
/// без пересчёта (PLAN-8).</summary>
public sealed record PositionCoefficient(string? Code, string Caption);

/// <summary>
/// Строка материала по конъюнктурному анализу (<c>ТЦ_…</c>, «материал по КА»). <see cref="Price" /> — сметная
/// цена единицы без НДС, как в смете (<c>PriceCurr/@MT</c>): потолок цены закупки. Отпускная цена, поставщик и
/// дата — из ссылки на КА; сметной цены в самом КА нет (PLAN-9).
/// </summary>
public sealed record MarketAnalysisLine(
    decimal? Price,
    string? Supplier,
    string? Inn,
    string? Kpp,
    decimal? SellingPrice,
    decimal? SellingPriceWithVat,
    decimal? Vat,
    DateOnly? SellingDate,
    string? Link);
