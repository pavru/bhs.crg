using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Причина исправления закрытого периода — одно правило у счёта и у накладной (issue #1340): обязательна и не
/// длиннее <see cref="Length" />. Два своих правила разошлись бы при первой правке одного из них, и одна и та
/// же причина получала бы у двух документов разные отказы.
/// </summary>
internal static class CorrectionReason
{
    /// <summary>Самая длинная причина: она ложится в журнал ядра, а там длинный текст обрезался бы молча.</summary>
    public const int Length = 1000;

    /// <param name="raw">Причина, как прислана.</param>
    /// <param name="of">Чья версия — родительным падежом: «счёта», «накладной».</param>
    public static string Checked(string? raw, string of)
    {
        var reason = raw?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new InvalidRequestException(
                $"Не названа причина исправления. Она ляжет в версию {of} и в журнал: без неё исправление " +
                "закрытого периода через год не объяснит никто.");
        if (reason.Length > Length)
            throw new InvalidRequestException(
                $"Причина длиннее {Length} знаков. Коротко назовите, что и почему исправлено; подробности — в примечании {of}.");
        return reason;
    }
}
