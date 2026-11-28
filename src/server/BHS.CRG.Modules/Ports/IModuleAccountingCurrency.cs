namespace BHS.CRG.Modules.Ports;

/// <summary>Действующий код ISO валюты учёта экземпляра, с тем же умолчанием, что у ядра.</summary>
public interface IModuleAccountingCurrency
{
    Task<string> CodeAsync(CancellationToken ct = default);
}
