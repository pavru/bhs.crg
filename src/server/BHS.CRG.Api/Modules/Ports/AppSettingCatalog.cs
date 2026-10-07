using BHS.CRG.Application.Settings;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Settings;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Ключи настроек экземпляра, которые знает эта сборка: два ключа ядра и всё, что объявили модули
/// (M1, issue #1070). Живёт в приложении, потому что только оно видит и то, и другое.
///
/// <para>Столкнуться ключ модуля с ключом ядра не может по форме: у модуля ключ из трёх частей
/// («модуль.объект.настройка», проверяет <see cref="ModuleSettingCatalog" /> при старте), у ядра —
/// из двух. Отдельной проверки поэтому нет: она была бы недостижимой (ревью PR #1249).</para>
/// </summary>
public sealed class AppSettingCatalog(ModuleSettingCatalog modules, ModuleRegistry registry) : IAppSettingCatalog
{
    public bool Accepts(string key, string value)
    {
        if (AppSettingKeys.IsKnown(key)) return value.Length <= AppSettingKeys.MaxValueLength;

        // Настройка модуля — и выключенного тоже: его значение лежит и ждёт включения. Значение
        // проверяет само объявление: копия могла прийти от сборки с другими границами.
        return modules.Find(key) is { } setting && setting.Refuse(value) is null;
    }

    public string Normalize(string key, string value) =>
        modules.Find(key) is { } setting && setting.Refuse(value) is null ? setting.Normalize(value) : value;

    public SettingChange? Change(string key, string? before, string? after)
    {
        if (modules.Find(key) is not { } setting) return null;

        var owner = registry.Enabled.Concat(registry.Disabled)
            .FirstOrDefault(m => string.Equals(m.Code, setting.Module, StringComparison.Ordinal));
        return ModuleSettingValues.Change(owner?.Title ?? setting.Module, setting, before, after);
    }
}

/// <summary>
/// Действующее значение настройки модуля и её смена — одно место на адрес администратора и на
/// восстановление копии: оба пишут в журнал, и «изменилось ли» у них обязано значить одно и то же.
/// </summary>
public static class ModuleSettingValues
{
    /// <summary>В базе лежит значение, которое эта версия не принимает.</summary>
    public static bool Stale(ModuleSetting setting, string? stored) =>
        stored is not null && setting.Refuse(stored) is not null;

    /// <summary>Действующее значение в хранимом виде: сохранённое годное, иначе умолчание.</summary>
    public static string Effective(ModuleSetting setting, string? stored) =>
        stored is null || Stale(setting, stored) ? setting.DefaultText : setting.Normalize(stored);

    /// <summary>
    /// Смена ДЕЙСТВУЮЩЕГО значения — словами для журнала; <c>null</c> — действует то же, что и было.
    ///
    /// <para>Сравниваются действующие значения, а не сохранённые строки (ревью PR #1249): сброс
    /// негодного «500» к умолчанию, запись «1» поверх несохранённого рубля и «1.00» поверх «1»
    /// строку в базе меняют, а настройку — нет, и запись «было 1,00 ₽, стало 1,00 ₽» была бы шумом.</para>
    /// </summary>
    public static SettingChange? Change(string moduleTitle, ModuleSetting setting, string? before, string? after)
    {
        var (was, now) = (Effective(setting, before), Effective(setting, after));
        return string.Equals(was, now, StringComparison.Ordinal)
            ? null
            : new SettingChange($"{moduleTitle}: {setting.Title}", setting.Display(was), setting.Display(now));
    }
}
