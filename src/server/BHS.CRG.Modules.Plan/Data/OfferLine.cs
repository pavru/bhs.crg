using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Строка КП — перенесённая строка редакции (change <c>plan-13-offers</c>, ТЗ PLAN-10): наименование, объём и
/// единица — как в ЛСР, вид работы — справка и пометка «не сопоставлена», цена продажи — посчитанная.
///
/// <para><b>Объём и единица — объём расценки и её единица</b> (<c>SourceVolume</c>, <c>SourceUnit</c> строки
/// редакции): на этот объём посчитан итог строки ЛСР, и база наценки «итог / объём» верна только на нём
/// (решение В-8).</para>
///
/// <para>⚠️ Себестоимости в строке нет (ТЗ PLAN-11): база наценки читается из строки редакции при расчёте и
/// не запоминается — иначе таблица КП стала бы вторым местом, где лежит себестоимость.</para>
/// </summary>
public sealed class OfferLine
{
    private OfferLine() { }

    public Guid Id { get; private set; }

    public Guid OfferId { get; private set; }

    public int Order { get; private set; }

    /// <summary>Строка редакции-источника; <c>null</c> — строка из прайс-листа (<c>plan-14</c>).</summary>
    public Guid? RevisionRowId { get; private set; }

    /// <summary>Вид работы, как был у строки сметы при переносе; <c>null</c> — не сопоставлена.</summary>
    public Guid? WorkTypeId { get; private set; }

    /// <summary>Строка-материал ЛСР: вида работы ей не нужно, и «не сопоставленной» она не бывает (<c>plan-31</c>).</summary>
    public bool Material { get; private set; }

    public string? LsrSectionCode { get; private set; }

    public string? LsrSectionName { get; private set; }

    /// <summary>Номер позиции в файле, как его показывает смета.</summary>
    public string? Number { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal? Volume { get; private set; }

    public string? Unit { get; private set; }

    /// <summary>Своя наценка строки в процентах; <c>null</c> — действует наценка раздела или предложения.</summary>
    public decimal? Markup { get; private set; }

    /// <summary>Цена продажи за единицу; <c>null</c> — у строки нет объёма или итога в смете.</summary>
    public decimal? Price { get; private set; }

    /// <summary>Цена прайс-листа на момент расчёта, до наценки КП.</summary>
    public decimal? PriceListPrice { get; private set; }
    public DateOnly? PriceListValidFrom { get; private set; }
    public Guid? PriceListUnitId { get; private set; }

    public bool MissingPrice => RevisionRowId is null && PriceListPrice is null;

    /// <summary>Сумма строки по цене продажи.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Строка-работа без вида работы — та же пометка, что в смете (ТЗ PLAN-4).</summary>
    public bool Unmatched => !Material && WorkTypeId is null;

    internal static OfferLine FromRevisionRow(Guid offerId, EstimateRow row) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfferId = offerId,
            Order = row.Order,
            RevisionRowId = row.Id,
            WorkTypeId = row.WorkTypeId,
            Material = row.IsMaterial,
            LsrSectionCode = row.LsrSectionCode,
            LsrSectionName = row.LsrSectionName,
            Number = row.Number ?? row.NumberInSection,
            Name = row.Name,
            Volume = row.SourceVolume,
            Unit = row.SourceUnit,
        };

    internal void ChangeMarkup(decimal? markup) => Markup = markup;

    internal static OfferLine FromWorkType(Guid offerId, int order, ModuleWorkType work, decimal volume) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfferId = offerId,
            Order = order,
            WorkTypeId = work.Id,
            Name = work.Name ?? work.DisplayName,
            Volume = volume,
            PriceListUnitId = work.UnitId,
            Unit = PlanText.Clip(work.UnitName, EstimateRow.UnitLength),
        };

    internal void SetPriceListEntry(PriceListEntry? entry)
    {
        if (entry is not null && (entry.WorkTypeId != WorkTypeId || entry.UnitId != PriceListUnitId))
            throw new InvalidOperationException("Вид работы и единица цены должны совпадать со строкой КП.");
        PriceListPrice = entry?.Price;
        PriceListValidFrom = entry?.ValidFrom;
    }

    /// <summary>Цена и сумма — их кладёт только расчёт (<see cref="Offers.OfferPricing" />).</summary>
    internal void Reprice(decimal? price, decimal amount)
    {
        Price = price;
        Amount = amount;
    }
}

/// <summary>Наценка раздела ЛСР в КП (ТЗ PLAN-11): действует у строк раздела, где своей наценки нет.</summary>
public sealed class OfferSectionMarkup
{
    private OfferSectionMarkup() { }

    public Guid OfferId { get; private set; }

    /// <summary>Код раздела ЛСР, как у строк («1»).</summary>
    public string SectionCode { get; private set; } = string.Empty;

    public decimal Markup { get; private set; }

    internal static OfferSectionMarkup Create(Guid offerId, string sectionCode, decimal markup) =>
        new() { OfferId = offerId, SectionCode = sectionCode, Markup = markup };

    internal void Change(decimal markup) => Markup = markup;
}
