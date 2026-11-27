using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class DeviceActions : IModuleActivityActions
{
    // Достаточно доступа к Work и журналу: цель содержит только идентификатор устройства.
    public static readonly ModuleActivityAction Revoked = new("work.device.revoked", "Доступ полевого устройства отозван");

    public IReadOnlyList<ModuleActivityAction> Actions => [Revoked];

    public static string ReasonLabel(DeviceRevocation reason) => reason switch
    {
        DeviceRevocation.Replacement => "Замена телефона",
        DeviceRevocation.Dismissal => "Увольнение",
        DeviceRevocation.Lost => "Утеря телефона",
        _ => reason.ToString(),
    };
}
