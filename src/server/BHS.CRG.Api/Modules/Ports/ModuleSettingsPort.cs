using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Переходник настроек модуля (M1, issue #1070): значения лежат в настройках экземпляра ядра —
/// той же таблице, что пояс компании, под ключом с префиксом модуля. Отдельного хранилища нет
/// нарочно: резервная копия эту таблицу уже несёт, а схема выключенного модуля в копию не входит.
/// </summary>
public sealed class ModuleSettingsPort(IAppSettingsStore store, ModuleSettingCatalog catalog) : IModuleSettings
{
    // Прочитанное за запрос: модуль спрашивает допуск на каждом расчёте баланса, а расчётов в одном
    // чтении реестра — по числу счетов. Между запросами не живёт: порт Scoped.
    private readonly Dictionary<string, string?> _read = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(ModuleSetting<T> setting, CancellationToken ct = default)
    {
        // Поле есть, а в Settings модуля его нет: у такой настройки нет ни экрана, ни проверки при
        // старте, и читалась бы она умолчанием всегда. Отказ здесь — первый же вызов в разработке.
        if (!catalog.Declares(setting))
            throw new InvalidOperationException(
                $"Настройка «{setting.Key}» не объявлена: впишите её в Settings модуля «{setting.Module}». " +
                "Без объявления её не видит администратор и не узнаёт резервная копия.");

        if (!_read.TryGetValue(setting.Key, out var stored))
            _read[setting.Key] = stored = await store.GetAsync(setting.Key, ct);

        return setting.Read(stored);
    }
}
