using System.Globalization;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>В какой единице пришли части: строки, как в файле («12 · 100 м»), или позиции плана («1 200 м»).</summary>
public enum PartUnit
{
    Row,
    Plan,
}

/// <summary>Как строка распределена: целиком на разделы, частично (остаток — стройка в целом), явной «стройкой в
/// целом» или никак — решения ещё не было.</summary>
public enum LineStatus
{
    Distributed,
    Partial,
    Whole,
    Undistributed,
}

/// <summary>Чем распределена строка: своими частями, правилом раздела ЛСР или ничем.</summary>
public enum LineSource
{
    Parts,
    Rule,
    None,
}

/// <summary>Что строки нужно знать для распределения: объём в обеих единицах и как её назвать в отказе.</summary>
/// <param name="Factor">Коэффициент из единицы строки в единицу плана; <c>null</c> — строка не сопоставлена.</param>
/// <param name="Volume">Объём в единице плана — как его хранит строка (<c>SourceVolume × Factor</c>).</param>
public sealed record LineFacts(Guid RowId, string Number, string Name, decimal? SourceVolume, decimal? Factor, decimal? Volume)
{
    /// <summary>Части задать можно: у строки есть объём. Без объёма — только правилом раздела.</summary>
    public bool Splittable => SourceVolume is > 0m;

    public static LineFacts Of(EstimateRow row) =>
        new(row.Id, NumberOf(row), row.Name, row.SourceVolume, row.Factor, row.Volume);

    /// <summary>Чем строку назвать человеку: номер файла, иначе номер в разделе, иначе порядок.</summary>
    public static string NumberOf(EstimateRow row) =>
        row.Number ?? row.NumberInSection ?? row.Order.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Часть из запроса — в его единице.</summary>
public sealed record PartRequest(Guid SectionId, decimal Quantity);

/// <summary>Часть к записи — в единице строки, шесть знаков.</summary>
public sealed record StoredPart(Guid SectionId, decimal Quantity);

/// <summary>Сколько строк в каком состоянии: <c>Distributed + Partial + Whole + Undistributed = Rows</c>.</summary>
public sealed record DistributionTally(int Rows, int Distributed, int Partial, int Whole, int Undistributed)
{
    public static readonly DistributionTally Empty = new(0, 0, 0, 0, 0);

    public DistributionTally Add(LineStatus status, int count = 1) => status switch
    {
        LineStatus.Distributed => this with { Rows = Rows + count, Distributed = Distributed + count },
        LineStatus.Partial => this with { Rows = Rows + count, Partial = Partial + count },
        LineStatus.Whole => this with { Rows = Rows + count, Whole = Whole + count },
        _ => this with { Rows = Rows + count, Undistributed = Undistributed + count },
    };
}

/// <summary>
/// Арифметика распределения строки (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1, COST-13) — без базы, чтобы
/// счётчики, чтение и запись говорили одно и то же.
///
/// <para><b>Действующее распределение строки</b>: части, если они есть, иначе правило её раздела ЛСР, иначе «не
/// распределено». Явная «стройка в целом» — решение, в счётчик «не распределено» она не входит.</para>
///
/// <para><b>Баланс</b> — точно в той единице, в которой части пришли (<c>decimal</c>, без допуска): в единице
/// плана против <see cref="LineFacts.Volume" />, в единице строки против <see cref="LineFacts.SourceVolume" />.
/// Потом части делятся на коэффициент и округляются до шести знаков, а ошибка округления уходит в наибольшую
/// часть: сумма хранимых не превышает объёма строки, а при полном вводе равна ему ровно. Иначе «6 × 1 м» при
/// коэффициенте 6 хранились бы как 1,000002 и получали ложный отказ, а полный ввод оставлял бы остаток.</para>
/// </summary>
public static class DistributionMath
{
    public const int Decimals = 6;

    /// <summary>Состояние строки по её действующему распределению.</summary>
    /// <param name="partsSum">Сумма частей в единице строки; частей нет — <c>null</c>.</param>
    public static LineStatus Status(decimal? sourceVolume, decimal? partsSum, DistributionTarget? rule) =>
        partsSum is { } sum ? (sum >= (sourceVolume ?? 0m) ? LineStatus.Distributed : LineStatus.Partial)
        : rule is null ? LineStatus.Undistributed
        : rule.Kind == DistributionTargetKind.Whole ? LineStatus.Whole
        : LineStatus.Distributed;

    public static LineSource Source(bool hasParts, DistributionTarget? rule) =>
        hasParts ? LineSource.Parts : rule is null ? LineSource.None : LineSource.Rule;

    /// <summary>Количество в единице плана — для показа: «7 · 100 м = 700 м».</summary>
    public static decimal? ToPlan(decimal quantity, decimal? factor) =>
        factor is { } k ? Math.Round(quantity * k, Decimals, MidpointRounding.ToEven) : null;

    /// <summary>
    /// Части запроса — к записи, в единице строки; или отказ строки с причиной. Пустой набор — не «к правилу»
    /// (это <c>parts: null</c>), а ошибка запроса: его отвергает разбор.
    /// </summary>
    public static (IReadOnlyList<StoredPart>? Parts, BalanceError? Error) Store(
        LineFacts row, PartUnit unit, IReadOnlyList<PartRequest> parts)
    {
        var sum = parts.Sum(p => p.Quantity);
        BalanceError Refuse(string code, decimal? volume, string why) =>
            new(row.RowId, row.Number, row.Name, code, sum, volume, unit, row.Factor, $"строка {row.Number} «{row.Name}»: {why}");

        if (!row.Splittable)
            return (null, Refuse("no-volume", null,
                "у строки нет объёма — её распределяет только правило раздела ЛСР, целиком"));
        if (parts.GroupBy(p => p.SectionId).Any(g => g.Count() > 1))
            return (null, Refuse("section-twice", Limit(row, unit), "раздел стройки назван в частях дважды"));
        if (parts.Any(p => p.Quantity <= 0m))
            return (null, Refuse("not-positive", Limit(row, unit), "часть должна быть больше нуля"));
        if (unit == PartUnit.Plan && (row.Factor is not > 0m || row.Volume is null))
            return (null, Refuse("not-matched", null,
                "строка не сопоставлена — единицы плана у неё нет, части принимаются в единице строки"));

        var limit = Limit(row, unit)!.Value;
        if (sum > limit)
            return (null, Refuse("over", limit, $"сумма частей {Both(sum, unit, row)} больше объёма {Both(limit, unit, row)}"));

        var factor = unit == PartUnit.Plan ? row.Factor!.Value : 1m;
        var volume = row.SourceVolume!.Value;
        var stored = parts.Select(p => Math.Round(p.Quantity / factor, Decimals, MidpointRounding.ToEven)).ToArray();
        var storedSum = stored.Sum();
        // Полный ввод — ровно объём строки; иначе сумма не выше объёма: округление не даёт ни остатка, ни превышения.
        var target = sum == limit ? volume : Math.Min(storedSum, volume);
        if (target != storedSum)
        {
            var largest = Array.IndexOf(stored, stored.Max());
            stored[largest] += target - storedSum;
        }
        if (stored.Any(q => q <= 0m))
            return (null, Refuse("too-small", limit, $"часть меньше {Step} единицы строки — её не записать"));

        return ([.. parts.Select((p, i) => new StoredPart(p.SectionId, stored[i]))], null);
    }

    private static decimal? Limit(LineFacts row, PartUnit unit) => unit == PartUnit.Plan ? row.Volume : row.SourceVolume;

    /// <summary>«13 · 100 м = 1 300 м»: число в обеих единицах, как его называет отказ.</summary>
    private static string Both(decimal quantity, PartUnit unit, LineFacts row)
    {
        if (row.Factor is not > 0m) return Number(quantity);
        var inRow = unit == PartUnit.Row ? quantity : Math.Round(quantity / row.Factor.Value, Decimals, MidpointRounding.ToEven);
        var inPlan = unit == PartUnit.Plan ? quantity : ToPlan(quantity, row.Factor)!.Value;
        return $"{Number(inRow)} в единице строки = {Number(inPlan)} в единице плана";
    }

    private static string Number(decimal value) => value.ToString("0.######", Russian);

    private static readonly string Step = 0.000001m.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"));

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
}
