namespace BHS.CRG.Modules.Settings;

/// <summary>
/// Все настройки, объявленные модулями СБОРКИ, — и включёнными, и выключенными (задача M1, issue #1070).
///
/// <para>По всей сборке, а не по включённым: настройка выключенного модуля лежит в базе и приезжает
/// в резервной копии, и узнать её ключ обязан тот же каталог — иначе восстановление выбросило бы её
/// как незнакомую, а модуль после включения молча начал бы с умолчания.</para>
///
/// <para>Собирается при старте: негодное объявление роняет запуск, а не первый заход администратора
/// на экран настроек.</para>
/// </summary>
public sealed class ModuleSettingCatalog
{
    private readonly Dictionary<string, ModuleSetting> _byKey = new(StringComparer.Ordinal);

    public ModuleSettingCatalog(IEnumerable<IAppModule> available)
    {
        var problems = new List<string>();

        foreach (var module in available)
        {
            foreach (var setting in module.Settings)
            {
                if (setting.Validate() is { } invalid)
                {
                    problems.Add($"модуль «{module.Code}»: {invalid}");
                    continue;
                }

                // Префикс — код владельца: по нему ключ не сталкивается ни с ключами ядра, ни с
                // соседним модулем, и по нему же видно, чья настройка лежит в базе.
                if (!string.Equals(setting.Module, module.Code, StringComparison.Ordinal))
                    problems.Add($"модуль «{module.Code}»: ключ «{setting.Key}» обязан начинаться с «{module.Code}.»");
                else if (!_byKey.TryAdd(setting.Key, setting))
                    problems.Add($"модуль «{module.Code}»: ключ «{setting.Key}» объявлен дважды");
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Негодные объявления настроек модулей:\n" + string.Join("\n", problems));
    }

    /// <summary>Объявление по ключу; <c>null</c> — такого ключа не объявляет ни один модуль сборки.</summary>
    public ModuleSetting? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// То ли это объявление, что вписано в <c>Settings</c> модуля. Настройка, заведённая полем и не
    /// вписанная, читалась бы умолчанием всегда: экрана у неё нет, и сохранить её нечем.
    /// </summary>
    public bool Declares(ModuleSetting setting) => ReferenceEquals(Find(setting.Key), setting);
}
