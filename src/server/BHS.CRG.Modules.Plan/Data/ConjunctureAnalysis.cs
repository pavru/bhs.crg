using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Конъюнктурный анализ — приложение к смете (change <c>plan-11-conjuncture</c>, ТЗ PLAN-9, PLAN-7): свидетельство
/// файла ГРАНД-Сметы, которым сметчик обосновал цену материала вне сборника. Система его хранит и показывает, а не
/// ведёт: правки нет, обратно в ГРАНД-Смету ничего не пишется (PLAN-8).
///
/// <para><b>Заводит его только решение человека</b> по отчёту разбора — тем же импортом, что ЛСР (<c>plan-07</c>):
/// импорт уходит, а файл, версии и разобранное переходят сюда. <see cref="ImportKey" /> — импорт, из которого
/// анализ заведён: повтор принятия того же импорта называет этот анализ, а второго не создаёт.</para>
///
/// <para>Поставщики — текстом из файла у каждого предложения, а не ссылкой на организацию ядра: анализ —
/// свидетельство файла, заводить по нему организации в справочник незачем (design).</para>
/// </summary>
public sealed class ConjunctureAnalysis : EstimateAttachment
{
    private ConjunctureAnalysis() { }

    /// <summary>Дата текущих цен анализа (<c>DocDates/@CurrPriceDate</c>).</summary>
    public DateOnly? CurrentPriceDate { get; private set; }

    public List<ConjunctureItem> Items { get; private set; } = [];

    public static ConjunctureAnalysis From(EstimateImport import, ParsedConjuncture model, DateTimeOffset now, Guid? by, string? byName)
    {
        var analysis = new ConjunctureAnalysis { CurrentPriceDate = model.Requisites.CurrentPriceDate };
        analysis.Fill(import, model.Requisites.Number, model.Requisites.Name, model.Requisites.CreationDate,
            model.Positions.Count(), model.PositionsInFile, now, by, byName);
        var order = 0;
        foreach (var chapter in model.Chapters)
            foreach (var position in chapter.Positions)
                analysis.Items.Add(ConjunctureItem.From(analysis.Id, ++order, chapter, position));
        return analysis;
    }
}

/// <summary>Как выбрана цена позиции анализа.</summary>
public enum ConjunctureSelection
{
    /// <summary>Минимальная отпускная без НДС среди предложений — правило Методики 421/пр, когда в файле не выбрано иное.</summary>
    Min,

    /// <summary>Выбрана в файле (<c>MainSubPosition</c>) — не обязательно минимальная.</summary>
    Chosen,

    /// <summary>Ни у одного предложения нет цены — выбрать нечего.</summary>
    None,
}

/// <summary>
/// Позиция конъюнктурного анализа: что обосновано (наименование, единица) и какая цена выбрана. Сметной цены в
/// файле КА нет (<c>plan-05</c>): выбранная — отпускная без НДС выбранного предложения.
/// </summary>
public sealed class ConjunctureItem
{
    public const int NumberLength = 100;
    public const int UnitLength = 100;

    private ConjunctureItem() { }

    public Guid Id { get; private set; }

    public Guid AnalysisId { get; private set; }

    /// <summary>Порядок по файлу — сквозной, по нему позиции читаются страницами.</summary>
    public int Order { get; private set; }

    public int ChapterIndex { get; private set; }

    public string ChapterCaption { get; private set; } = string.Empty;

    /// <summary>Номер позиции по нумерации анализа.</summary>
    public string Number { get; private set; } = string.Empty;

    public string Caption { get; private set; } = string.Empty;

    public string? Units { get; private set; }

    /// <summary>Позиция в анализе не учитывается (<c>Inactive</c>) — показывается, но цены не обосновывает.</summary>
    public bool IsInactive { get; private set; }

    public ConjunctureSelection Selection { get; private set; }

    /// <summary>Выбранное предложение — порядковый номер среди <see cref="Offers" />, с единицы.</summary>
    public int? SelectedOffer { get; private set; }

    /// <summary>Выбранная цена — отпускная без НДС выбранного предложения. Деньги сметы: только с <c>plan.estimate.cost</c>.</summary>
    public decimal? SelectedPrice { get; private set; }

    public List<ConjunctureOffer> Offers { get; private set; } = [];

    internal static ConjunctureItem From(Guid analysisId, int order, ConjunctureChapter chapter, ConjuncturePosition position)
    {
        var item = new ConjunctureItem
        {
            Id = Guid.NewGuid(),
            AnalysisId = analysisId,
            Order = order,
            ChapterIndex = chapter.Index,
            ChapterCaption = chapter.Caption,
            Number = PlanText.Clip(position.Number, NumberLength) ?? string.Empty,
            Caption = position.Caption,
            Units = PlanText.Clip(position.Units, UnitLength),
            IsInactive = position.IsInactive,
        };
        for (var i = 0; i < position.Offers.Count; i++)
            item.Offers.Add(ConjunctureOffer.From(item.Id, i + 1, position.Offers[i]));

        if (position.ChosenOffer is { } chosen)
        {
            item.Selection = ConjunctureSelection.Chosen;
            item.SelectedOffer = chosen;
            item.SelectedPrice = position.Offers[chosen - 1].SellingPrice;
        }
        else if (position.MinSellingPrice is { } min)
        {
            item.Selection = ConjunctureSelection.Min;
            // Равных минимальных несколько — выбранным назван первый по файлу: цена у них одна.
            item.SelectedOffer = item.Offers.First(o => o.Price == min).Order;
            item.SelectedPrice = min;
        }
        else
            item.Selection = ConjunctureSelection.None;
        return item;
    }
}
