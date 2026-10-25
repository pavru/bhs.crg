namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>Что запомнилось сопоставлением — шифра или ресурса (<c>plan-08</c>); уезжает в ответе, чтобы экран
/// сказал это словами.</summary>
/// <param name="Remembered">Сколько записано: новых и перенацеленных вместе.</param>
/// <param name="Replaced">Сколько из них заменили запомненное раньше другим выбором.</param>
/// <param name="Failed">Запомнить НЕ УДАЛОСЬ, а сопоставление при этом записано (<c>memory.failed</c>). Ноль
/// запомненного без признака читался бы как «запоминать было нечего».</param>
public sealed record MatchMemory(int Remembered, int Replaced, bool Failed = false)
{
    public static readonly MatchMemory Nothing = new(0, 0);
}
