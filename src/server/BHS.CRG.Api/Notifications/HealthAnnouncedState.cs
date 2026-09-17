using BHS.CRG.Application.Notifications;

namespace BHS.CRG.Api.Notifications;

/// <summary>
/// Объявленное мониторингом состояние компонентов — строкой в <c>service_state</c> (issue #920).
///
/// Хранится по той же причине, что и уведомлённая версия у проверки обновлений: «объявили отказ» —
/// факт, а не состояние. Держи мы его в памяти, каждый перезапуск повторял бы объявление, а при
/// обновлении перезапуск происходит по определению.
/// </summary>
public class HealthAnnouncedState
{
    public const string Key = "health-announced";

    /// <summary>
    /// Код компонента → объявленное состояние. Строкой, а не числом: хранилище служебного состояния
    /// пишет перечисления числами, и перестановка значений в <see cref="HealthState"/> молча
    /// превратила бы сохранённый отказ в норму.
    /// </summary>
    public Dictionary<string, string> Components { get; set; } = [];

    public static HealthAnnouncedState From(IReadOnlyDictionary<string, HealthState> announced) => new()
    {
        Components = announced.ToDictionary(a => a.Key, a => a.Value.ToString()),
    };

    /// <summary>Непонятное значение пропускается: испорченная запись не должна объявлять ничего.</summary>
    public IReadOnlyDictionary<string, HealthState> ToStates() => Components
        .Where(c => Enum.TryParse<HealthState>(c.Value, ignoreCase: false, out var s)
                    && s is HealthState.Up or HealthState.Down)
        .ToDictionary(c => c.Key, c => Enum.Parse<HealthState>(c.Value));
}
