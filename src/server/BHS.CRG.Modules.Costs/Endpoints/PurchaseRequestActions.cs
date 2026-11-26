using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed class PurchaseRequestActions : IModuleActivityActions
{
    public static readonly ModuleActivityAction Returned =
        new("costs.request.returned", "Заявка на закупку возвращена в черновик", CostsModule.RequestRead);

    public IReadOnlyList<ModuleActivityAction> Actions => [Returned];
}
