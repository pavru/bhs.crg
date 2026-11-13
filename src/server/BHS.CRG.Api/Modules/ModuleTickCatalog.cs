using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules;

/// <summary>Такт включённого модуля и постоянная цель его задачи.</summary>
public sealed record ModuleTickEntry(string Module, ModuleTick Tick, Guid TargetId);

/// <summary>
/// Такты ВКЛЮЧЁННЫХ модулей (ТЗ WORK-41, work-08) — собираются и проверяются при старте: негодное
/// объявление обязано ронять запуск, а не молчать до первого срока.
///
/// <para>Только включённые, в отличие от таблиц и настроек: у выключенного модуля обработчиков нет, и его
/// такт ставил бы задачи, которые некому выполнить. Данные выключенного модуля такт не трогает вовсе.</para>
///
/// <para>Проверка здесь — всё, что видно по объявлению: повтор кода, период, операция своего модуля.
/// Есть ли обработчик, видно только из собранного контейнера, в области служб (обработчики scoped), —
/// это <see cref="EnsureHandled" />, его зовёт старт.</para>
/// </summary>
public sealed class ModuleTickCatalog
{
    /// <summary>
    /// Пространство имён UUID v5 для целей тактов. Постоянное: цель обязана совпадать между
    /// перезапусками и версиями — по ней ищется «когда ставили последнюю» и держится «одна активная».
    /// </summary>
    private static readonly Guid TargetNamespace = new("3f8c2a52-7d0e-4f39-9a51-0b6c1d2e8f74");

    public ModuleTickCatalog(ModuleRegistry registry) : this(registry.Enabled) { }

    /// <param name="enabled">Включённые модули. Отдельный вход — ради проб: каталогу пробного модуля
    /// не нужен реестр поставки.</param>
    public ModuleTickCatalog(IEnumerable<IAppModule> enabled)
    {
        var all = enabled.ToList();
        var broken = new List<string>();

        foreach (var module in all)
        {
            foreach (var tick in module.Ticks)
                broken.AddRange(tick.Problems(module.Code).Select(p => $"модуль «{module.Code}»: {p}"));
            foreach (var twice in module.Ticks.GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                broken.Add($"модуль «{module.Code}»: такт «{twice.Key}» объявлен дважды");
        }

        if (broken.Count > 0)
            throw new InvalidOperationException(
                "Такты модулей объявлены негодно: " + string.Join("; ", broken) + ".");

        All = [.. all.SelectMany(m => m.Ticks.Select(t => new ModuleTickEntry(m.Code, t, TargetOf(m.Code, t.Code))))];
    }

    public IReadOnlyList<ModuleTickEntry> All { get; }

    /// <summary>
    /// Задача ли это такта: цель такта, его операция и без владельца. Владелец проверяется, потому что
    /// цель — просто идентификатор: модуль, поставивший СВОЮ работу над ним портом из запроса, — не такт,
    /// и выдавать его за систему значило бы спрятать человека, который нажал.
    /// </summary>
    public ModuleTickEntry? Find(Guid targetId, string operation, Guid userId) =>
        userId != Guid.Empty
            ? null
            : All.FirstOrDefault(e => e.TargetId == targetId
                && string.Equals(e.Tick.Operation, operation, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Каждая операция такта имеет обработчик. Тем же правилом, что постановка
    /// (<see cref="ModuleWork.EnsureHandled" />), но при старте и с названием модуля и такта: иначе такт
    /// без обработчика отказывал бы на каждом сроке — уведомлением раз в период, а не остановкой запуска.
    /// </summary>
    public void EnsureHandled(IEnumerable<IModuleJobHandler> handlers)
    {
        var all = handlers.ToList();
        foreach (var entry in All)
        {
            try
            {
                ModuleWork.EnsureHandled(entry.Tick.Operation, all);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Такт «{entry.Tick.Code}» («{entry.Tick.Title}») модуля «{entry.Module}» не может " +
                    $"работать: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Постоянная цель такта — UUID v5 из «модуль:код» (RFC 9562, SHA-1).</summary>
    public static Guid TargetOf(string module, string code)
    {
        var name = Encoding.UTF8.GetBytes($"{module}:{code}".ToLowerInvariant());
        var ns = TargetNamespace.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. ns, .. name]);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // версия 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // вариант RFC
        return new Guid(bytes, bigEndian: true);
    }
}
