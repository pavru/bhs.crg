using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

public sealed class ModuleAccountingCurrencyPort(IAppSettingsStore settings) : IModuleAccountingCurrency
{
    public async Task<string> CodeAsync(CancellationToken ct = default) =>
        AccountingCurrencies.Effective(await settings.GetAsync(AppSettingKeys.AccountingCurrency, ct));
}
