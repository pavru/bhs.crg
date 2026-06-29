using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Settings;

/// <summary>Синглтон-строка с управляемыми из UI настройками интеграций (JSON).</summary>
public class IntegrationSettingsEntity : Entity
{
    public JsonDocument Data { get; private set; } = null!;

    private IntegrationSettingsEntity() { }

    public static IntegrationSettingsEntity Create(JsonDocument data) => new() { Data = data };

    public void Update(JsonDocument data)
    {
        Data = data;
        TouchUpdatedAt();
    }
}
