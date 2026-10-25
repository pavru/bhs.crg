using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Общее у списков запомненного — шифров (<see cref="RateMatchListEndpoints" />) и ресурсов
/// (<see cref="ResourceMatchListEndpoints" />): порция и отбор по словам файла (<c>plan-08</c>).
/// </summary>
internal static class MatchLists
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    /// <summary>Порция: <c>skip</c> не меньше нуля, <c>take</c> от 1 до <see cref="MaxTake" />; иначе отказ словами.</summary>
    public static int Take(int skip, int? take) =>
        skip < 0 || take is <= 0 or > MaxTake
            ? throw new InvalidRequestException($"Порция списка задана неверно: «skip» не меньше нуля, «take» от 1 до {MaxTake}.")
            : take ?? DefaultTake;

    /// <summary>Образец <c>ILIKE</c> «содержит» — знаки образца экранированы символом <c>\</c>: «100%» ищется как
    /// текст, а не как «всё, что начинается со 100».</summary>
    public static string Contains(string query) =>
        "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
