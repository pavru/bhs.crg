using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Modules;

public sealed class DocumentTypeCapabilities(ModuleRegistry modules) : IDocumentTypeCapabilities
{
    public bool SupportsAcceptance(DocumentType type)
    {
        if (type.EditLevel != SchemaEditLevel.Extendable) return false;
        var owner = modules.Find(type.Module) ?? modules.Disabled.FirstOrDefault(module =>
            string.Equals(module.Code, type.Module, StringComparison.OrdinalIgnoreCase));
        var storage = type.Storage == TypeStorage.ModuleTable ? ModuleStorage.ModuleTable : ModuleStorage.SharedObject;
        return owner?.RecordTypes.Any(declared =>
            declared.AcceptanceFields && declared.Level == ModuleSchemaLevel.Extendable && declared.Storage == storage
            && string.Equals(declared.Code, type.Code, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
