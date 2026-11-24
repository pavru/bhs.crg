using System.Text.Json;

namespace BHS.CRG.Modules.Ports;

/// <summary>Действующие поля для приёмки собственного типа модуля.</summary>
public interface IModuleAcceptanceFields
{
    Task<ModuleAcceptanceSchema> ReadAsync(string module, string typeCode, CancellationToken ct);
    Task<JsonElement?> FormAsync(string module, string typeCode, CancellationToken ct);
}

/// <summary>Один снимок требований к приёмке для чтения отчётов и их строк.</summary>
public abstract class ModuleAcceptanceSchema
{
    public abstract bool HasFields { get; }
    public abstract IReadOnlyList<ModuleAcceptanceField> Missing(JsonElement data);
}

public sealed record ModuleAcceptanceField(string Key, string Title);
