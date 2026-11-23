using System.Text.Json;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Printing;

public interface IModulePrintContext
{
    /// <summary>JSON-объект для Typst со всеми разрешёнными пользователю полями формы.</summary>
    Task<ModulePrintResult> BuildAsync(Guid recordId, IModuleUser user, CancellationToken ct);
}

public abstract record ModulePrintResult
{
    public sealed record Found(JsonElement Data) : ModulePrintResult;
    public sealed record Missing : ModulePrintResult;
    public sealed record Forbidden : ModulePrintResult;
}
