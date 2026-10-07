using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Settings;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Ключи настроек экземпляра, которые знает эта сборка: два ключа ядра и всё, что объявили модули
/// (M1, issue #1070). Живёт в приложении, потому что только оно видит и то, и другое.
/// </summary>
public sealed class AppSettingCatalog : IAppSettingCatalog
{
    private readonly ModuleSettingCatalog _modules;

    public AppSettingCatalog(ModuleSettingCatalog modules, BHS.CRG.Modules.ModuleRegistry registry)
    {
        _modules = modules;

        // Ключ модуля начинается с его кода, ключи ядра — с «company.» и «branding.». Столкнуться они
        // могут только если модуль назовут так же; проверяем сам ключ, а не договорённость об именах.
        var taken = registry.Enabled.Concat(registry.Disabled)
            .SelectMany(m => m.Settings)
            .Where(s => AppSettingKeys.IsKnown(s.Key))
            .Select(s => s.Key)
            .ToList();
        if (taken.Count > 0)
            throw new InvalidOperationException(
                "Ключи настроек модулей совпали с ключами ядра: " + string.Join(", ", taken) + ".");
    }

    public bool Accepts(string key, string value)
    {
        if (AppSettingKeys.IsKnown(key)) return value.Length <= AppSettingKeys.MaxValueLength;

        // Настройка модуля — и выключенного тоже: его значение лежит и ждёт включения. Значение
        // проверяет само объявление: копия могла прийти от сборки с другими границами.
        return _modules.Find(key) is { } setting && setting.Refuse(value) is null;
    }
}
