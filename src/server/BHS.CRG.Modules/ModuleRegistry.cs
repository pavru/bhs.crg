using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Modules;

/// <summary>
/// Какие модули включены на этом экземпляре. Набор задаёт ОПЕРАТОР ПОСТАВКИ переменной окружения
/// <c>Modules__Enabled</c> (ТЗ AUTH-17); администратор заказчика его не меняет, поэтому это
/// настройка запуска, а не строка в базе.
///
/// Реестр живёт в контейнере как singleton и отвечает на единственный вопрос — «включён ли модуль».
/// Спрашивают его ворота адресов, инструменты MCP, наборы данных и уведомления.
/// </summary>
public sealed class ModuleRegistry
{
    /// <summary>Набор по умолчанию: исполнительная документация. С него начиналась система.</summary>
    public const string DefaultCode = "id";

    private readonly Dictionary<string, IAppModule> _byCode;

    public ModuleRegistry(IReadOnlyList<IAppModule> enabled, IReadOnlyList<IAppModule> disabled)
    {
        Enabled = enabled;
        Disabled = disabled;
        _byCode = enabled.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Включённые модули в том порядке, в каком их перечислил оператор.</summary>
    public IReadOnlyList<IAppModule> Enabled { get; }

    /// <summary>
    /// Модули, которые есть в сборке, но на этом экземпляре не включены.
    ///
    /// Нужны не для порядка: их адреса всё равно регистрируются — отказом с названной причиной
    /// (<c>AppModuleExtensions.MapAppModules</c>). Незарегистрированный адрес отвечает пустым 404,
    /// неотличимым от опечатки в ссылке, а ТЗ требует отказа, который называет причину
    /// (OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    public IReadOnlyList<IAppModule> Disabled { get; }

    public bool IsEnabled(string code) => _byCode.ContainsKey(code);

    public IAppModule? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>
    /// Читает коды включённых модулей. Принимаются обе записи, потому что путей настройки два и
    /// они выглядят по-разному: массив (<c>Modules__Enabled__0=id</c>) приходит из конфигурации
    /// среды, строка через запятую (<c>Modules__Enabled=id,costs</c>) — из <c>.env</c> поставки,
    /// где массив записать нечем.
    ///
    /// Пустое значение — это НЕ «включить всё» и не «выключить всё»: пустая строка в <c>.env</c>
    /// появляется от невычищенной правки, и оба толкования были бы тихими. Берём умолчание.
    ///
    /// ⚠️ Если настройка задана ОБЕИМИ записями сразу — отказ, а не выбор одной из них. Так
    /// бывает, когда массив лежит в appsettings, а поставка переопределяет набор строкой из
    /// <c>.env</c>: приоритет провайдеров здесь не работает, потому что значение и дети живут в
    /// разных местах ветки и не перекрывают друг друга. Любой молчаливый выбор означал бы
    /// «поднялись зелёными без модуля, который заказали» (поймано на ревью #968).
    /// </summary>
    public static IReadOnlyList<string> ReadEnabledCodes(IConfiguration configuration) =>
        ReadEnabledCodes(configuration, out _);

    /// <summary>
    /// То же, но сообщает, взято ли умолчание. Нужно ради внятного отказа: «не задано, а умолчания
    /// в сборке нет» и «названо то, чего нет» — разные неполадки, и лечатся они по-разному.
    /// </summary>
    public static IReadOnlyList<string> ReadEnabledCodes(IConfiguration configuration, out bool fromDefault)
    {
        var section = configuration.GetSection("Modules:Enabled");

        var fromArray = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var fromScalar = (section.Value ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();

        if (fromArray.Count > 0 && fromScalar.Count > 0)
            throw new InvalidOperationException(
                "Modules__Enabled задан и списком, и строкой сразу: " +
                $"список [{string.Join(", ", fromArray)}], строка «{section.Value}». " +
                "Одна из записей будет проигнорирована молча, поэтому уберите лишнюю — " +
                "обычно это список в appsettings, если набор модулей задаётся поставкой.");

        var codes = fromArray.Count > 0 ? fromArray : fromScalar!;

        var cleaned = codes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        fromDefault = cleaned.Count == 0;
        return fromDefault ? [DefaultCode] : cleaned;
    }
}
