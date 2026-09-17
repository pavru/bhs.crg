using System.Text.Json;
using BHS.CRG.Api.Notifications;
using BHS.CRG.Application.Notifications;

namespace BHS.CRG.Tests.Notifications;

/// <summary>
/// Сохранённое объявленное состояние (issue #920). Запись переживает перезапуски и обновления,
/// поэтому читаться она обязана и испорченной, и написанной другой версией — и ни в каком случае не
/// должна объявлять того, чего не было.
/// </summary>
public class HealthAnnouncedStateTests
{
    [Fact]
    public void Объявленное_переживает_запись_и_чтение()
    {
        var announced = new Dictionary<string, HealthState>
        {
            ["db"] = HealthState.Up,
            ["recognition.gemini"] = HealthState.Down,
        };

        var json = JsonSerializer.Serialize(HealthAnnouncedState.From(announced));
        var restored = JsonSerializer.Deserialize<HealthAnnouncedState>(json)!.ToStates();

        Assert.Equal(announced, restored);
    }

    [Fact]
    public void Состояние_хранится_строкой_а_не_числом()
    {
        // Перестановка значений в перечислении иначе молча превратила бы сохранённый отказ в норму.
        var json = JsonSerializer.Serialize(HealthAnnouncedState.From(
            new Dictionary<string, HealthState> { ["db"] = HealthState.Down }));

        Assert.Contains("\"Down\"", json);
    }

    [Theory]
    [InlineData("Flapping")]
    [InlineData("down")]
    [InlineData("")]
    [InlineData("Сломано")]
    [InlineData("7")]
    public void Непонятное_значение_не_объявляет_ничего(string stored)
    {
        var state = new HealthAnnouncedState { Components = { ["recognition.gemini"] = stored, ["db"] = "Up" } };

        var restored = state.ToStates();

        Assert.False(restored.ContainsKey("recognition.gemini"));
        Assert.Equal(HealthState.Up, restored["db"]);
    }
}
