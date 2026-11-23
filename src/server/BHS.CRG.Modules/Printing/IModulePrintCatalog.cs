using System.Text.Json;

namespace BHS.CRG.Modules.Printing;

/// <summary>Раскрывает ссылки на записи общих данных для печати в правах текущего пользователя.</summary>
public interface IModulePrintCatalog
{
    Task<JsonElement> ResolveAsync(JsonElement data, CancellationToken ct);
}
