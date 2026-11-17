using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Offers;

/// <summary>Какой уровень наценки действует у строки: её собственная, раздела ЛСР или предложения.</summary>
public enum MarkupLevel
{
    Line,
    Section,
    Offer,
}

/// <summary>Действующая наценка строки и её уровень — ответ называет оба (риск «наценки путают» в design).</summary>
public readonly record struct EffectiveMarkup(decimal Value, MarkupLevel Level);

/// <summary>Цена строки: за единицу (<c>null</c> — объёма нет) и сумма.</summary>
public readonly record struct LinePrice(decimal? Price, decimal Amount);

/// <summary>Суммы КП: строк, НДС (<c>null</c> — «без НДС») и «Итого» к оплате.</summary>
public readonly record struct OfferTotals(decimal LinesTotal, decimal? Vat, decimal Total);

/// <summary>
/// Расчёт денег КП — ОДНО место (change <c>plan-13-offers</c>, ТЗ PLAN-11, решения В-8 и В-9): решись В-8 или
/// В-9 иначе, правится этот класс и его тесты, а не адреса.
///
/// <list type="bullet">
///   <item><b>Наценка</b>: строка → раздел ЛСР → предложение, действует самая частная. Проценты, от 0 до 1000,
///   не точнее сотой.</item>
///   <item><b>База</b> (В-8) — итог строки ЛСР в текущих ценах с НР и СП (<c>EstimateRow.Total</c>), делённый на
///   объём расценки. Цена продажи = база × (1 + наценка / 100), до копейки; сумма строки — цена × объём, до
///   копейки. Строка без объёма (не назван или 0) цены за единицу не имеет: её сумма — итог × (1 + наценка).
///   Итога в смете нет — сумма 0, без цены.</item>
///   <item><b>НДС</b> (В-9 (а), как у счёта, <c>COST-7.2</c>) — с суммы строк, не по строкам: «в том числе» —
///   сумма × ставка / (100 + ставка), «сверху» — сумма × ставка / 100, до копейки. «Итого» — к оплате.</item>
/// </list>
///
/// <para>Округление — от нуля на половине (<see cref="MidpointRounding.AwayFromZero" />), как копейки в бумаге.</para>
/// </summary>
public static class OfferPricing
{
    public const decimal MaxMarkup = 1000m;

    public const decimal MaxVatRate = 100m;

    /// <summary>Наценка по правилам КП — или отказ, называющий, где она задана.</summary>
    /// <param name="where">Где наценка, в родительном или с предлогом: «предложения», «раздела 2».</param>
    public static decimal CheckedMarkup(decimal markup, string where)
    {
        if (markup < 0m || markup > MaxMarkup)
            throw new InvalidRequestException(
                $"Наценка {where} — {markup} %: допустимо от 0 до {MaxMarkup} %. Скидка наценкой не задаётся.");
        if (decimal.Round(markup, 2) != markup)
            throw new InvalidRequestException($"Наценка {where} — {markup} %: не точнее сотой доли процента.");
        return markup;
    }

    /// <summary>Режим и ставка НДС по правилам КП: ставка нужна «в том числе» и «сверху» и не нужна «без НДС».</summary>
    public static decimal? CheckedVat(OfferVatMode mode, decimal? rate)
    {
        if (mode == OfferVatMode.None)
            return rate is null ? null : throw new InvalidRequestException(
                "Ставка НДС названа, а режим — «без НДС». Уберите ставку либо выберите режим «в том числе» или «сверху».");
        if (rate is not { } r)
            throw new InvalidRequestException("Не названа ставка НДС — при режиме «в том числе» и «сверху» она обязательна.");
        if (r <= 0m || r > MaxVatRate)
            throw new InvalidRequestException($"Ставка НДС {r} %: допустимо больше 0 и не больше {MaxVatRate} %.");
        if (decimal.Round(r, 2) != r)
            throw new InvalidRequestException($"Ставка НДС {r} %: не точнее сотой доли процента.");
        return r;
    }

    /// <summary>Действующая наценка строки: своя, иначе раздела, иначе предложения.</summary>
    public static EffectiveMarkup Effective(
        decimal? lineMarkup, string? sectionCode, IReadOnlyDictionary<string, decimal> sectionMarkups, decimal offerMarkup)
    {
        if (lineMarkup is { } own) return new(own, MarkupLevel.Line);
        if (sectionCode is not null && sectionMarkups.TryGetValue(sectionCode, out var section))
            return new(section, MarkupLevel.Section);
        return new(offerMarkup, MarkupLevel.Offer);
    }

    /// <summary>Цена строки по базе (итог строки ЛСР и её объём) и действующей наценке.</summary>
    public static LinePrice Price(decimal? baseTotal, decimal? volume, decimal markup)
    {
        if (baseTotal is not { } total) return new(null, 0m);
        var factor = 1m + markup / 100m;
        if (volume is not { } v || v == 0m) return new(null, Money(total * factor));

        var price = Money(total / v * factor);
        return new(price, Money(price * v));
    }

    /// <summary>Суммы КП по суммам строк, режиму и ставке НДС.</summary>
    public static OfferTotals Totals(IEnumerable<decimal> amounts, OfferVatMode mode, decimal? rate)
    {
        var lines = amounts.Sum();
        return mode switch
        {
            // Ставку при режиме с НДС обеспечивает CheckedVat — у каждого входа, где режим выбирают.
            OfferVatMode.Included => new(lines, Money(lines * rate.GetValueOrDefault() / (100m + rate.GetValueOrDefault())), lines),
            OfferVatMode.OnTop => Plus(lines, Money(lines * rate.GetValueOrDefault() / 100m)),
            _ => new(lines, null, lines),
        };

        static OfferTotals Plus(decimal lines, decimal vat) => new(lines, vat, lines + vat);
    }

    /// <summary>
    /// Пересчитать КП целиком: цены строк по базам и наценкам, затем суммы. Базы — итоги строк редакции по
    /// идентификатору строки редакции; строки без строки-источника (прайс-лист, <c>plan-14</c>) здесь не бывают.
    /// </summary>
    public static void Reprice(Offer offer, IReadOnlyDictionary<Guid, decimal?> baseTotals)
    {
        var sections = offer.SectionMarkups.ToDictionary(s => s.SectionCode, s => s.Markup, StringComparer.Ordinal);
        foreach (var line in offer.Lines)
        {
            var markup = Effective(line.Markup, line.LsrSectionCode, sections, offer.Markup);
            var baseTotal = line.RevisionRowId is { } row ? baseTotals.GetValueOrDefault(row) : null;
            var price = Price(baseTotal, line.Volume, markup.Value);
            line.Reprice(price.Price, price.Amount);
        }

        var totals = Totals(offer.Lines.Select(l => l.Amount), offer.VatMode, offer.VatRate);
        offer.Reprice(totals.LinesTotal, totals.Vat, totals.Total);
    }

    /// <summary>Пересчитать только суммы КП — цены строк не тронуты (сменились режим или ставка НДС).</summary>
    public static void Retotal(Offer offer)
    {
        var totals = Totals(offer.Lines.Select(l => l.Amount), offer.VatMode, offer.VatRate);
        offer.Reprice(totals.LinesTotal, totals.Vat, totals.Total);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
