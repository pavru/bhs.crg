using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>
/// Разобранный конъюнктурный анализ (ТЗ PLAN-7, PLAN-9): поставщики, позиции и коммерческие предложения.
///
/// <para><b>Сметной цены в файле КА нет</b>: программа пишет только отпускные цены (без НДС и с НДС), ставку, дату
/// и поставщика, а сметную считает при показе. Поэтому у позиции — <see cref="ConjuncturePosition.MinSellingPrice" />,
/// минимальная <b>отпускная</b> цена без НДС, названная так; сметная цена материала — в строке ЛСР по КА
/// (<see cref="MarketAnalysisLine.Price" />). Хранение и показ КА — <c>plan-11</c>.</para>
/// </summary>
public sealed record ParsedConjuncture(
    GrandSmetaSource Source,
    ConjunctureRequisites Requisites,
    IReadOnlyList<ConjunctureSupplier> Suppliers,
    IReadOnlyList<ConjunctureChapter> Chapters,
    IReadOnlyList<Unparsed> Unparsed)
{
    [JsonIgnore]
    public IEnumerable<ConjuncturePosition> Positions => Chapters.SelectMany(c => c.Positions);

    [JsonIgnore]
    public int PositionsInFile => Chapters.Sum(c => c.Positions.Count) + Unparsed.Count;
}

public sealed record ConjunctureRequisites(
    string? Number,
    string? Name,
    string? Construction,
    string? Object,
    DateOnly? CreationDate,
    DateOnly? CurrentPriceDate);

/// <summary>Поставщик (<c>Counterparties/Counterparty</c>). <see cref="Number" /> — номер по порядку в списке: на
/// него ссылается предложение (<c>Item/@Supplier</c>).</summary>
public sealed record ConjunctureSupplier(int Number, string? Caption, string? Inn, string? Kpp, string? Kind);

public sealed record ConjunctureChapter(int Index, string Caption, IReadOnlyList<ConjuncturePosition> Positions);

/// <param name="ChosenOffer">Предложение, выбранное в файле (<c>Position/@MainSubPosition</c>, «номер выбранной в КА
/// вложенной позиции») — номер по порядку среди <see cref="Offers" />, с единицы; <c>null</c> — в файле не выбрано,
/// цена позиции — минимальная (<see cref="MinSellingPrice" />). Ни в одном образце подборки атрибута нет: отсчёт с
/// единицы взят из слова «номер» схемы и на файле не проверен (<c>plan-11</c>).</param>
public sealed record ConjuncturePosition(
    int IndexInChapter,
    string Number,
    string Caption,
    string? Units,
    bool IsInactive,
    IReadOnlyList<ConjunctureOffer> Offers,
    decimal? MinSellingPrice,
    int? ChosenOffer = null);

/// <summary>Коммерческое предложение: <see cref="SellingPrice" /> — отпускная цена без НДС (<c>OptPrice</c>),
/// <see cref="SellingPriceWithVat" /> — с НДС, <see cref="Vat" /> — ставка, %.</summary>
public sealed record ConjunctureOffer(
    string Number,
    string Caption,
    string? Units,
    ConjunctureSupplier? Supplier,
    decimal? SellingPrice,
    decimal? Vat,
    decimal? SellingPriceWithVat,
    DateOnly? SellingDate,
    string? Country,
    string? ResourceGroup,
    bool UseTransport);
