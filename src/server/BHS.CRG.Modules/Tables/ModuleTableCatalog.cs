using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Tables;

/// <summary>Объявленная таблица вместе с модулем, который её объявил.</summary>
/// <param name="Module">Код модуля.</param>
/// <param name="ModuleTitle">Название модуля — для причины «модуль выключен».</param>
public sealed record ModuleTableEntry(string Module, string ModuleTitle, ModuleTable Table)
{
    public string Address => ModuleTable.Address(Module, Table.Code);
}

/// <summary>
/// Таблицы модулей (ТЗ CORE-33, задача G1b, issue #1089) — собираются при СТАРТЕ из всех модулей
/// сборки, включая выключенные.
///
/// <para><b>Почему и выключенные.</b> Таблица выключенного модуля — штатное состояние: она отвечает
/// «модуль выключен», а её колонки приходят с этой причиной (ТЗ CORE-33, AUTH-19). Не знай ядро о ней
/// вовсе, ответом стал бы пустой 404, неотличимый от опечатки в адресе.</para>
///
/// <para><b>Негодное объявление останавливает старт и называет таблицу.</b> Собираются ВСЕ ошибки
/// всех таблиц: чинить по одной на перезапуск — это пять перезапусков там, где хватает одного (тот же
/// приём, что в <see cref="PermissionCatalog" />).</para>
/// </summary>
public sealed class ModuleTableCatalog
{
    private readonly Dictionary<string, ModuleTableEntry> _byAddress;

    /// <param name="corePermissions">Права ядра (<c>core.*</c>): готовый отбор вправе назвать и такое.</param>
    public ModuleTableCatalog(IEnumerable<IAppModule> modules, IEnumerable<AppPermission>? corePermissions = null)
    {
        var all = modules.ToList();
        All = [.. all.SelectMany(m => m.Tables.Select(t => new ModuleTableEntry(m.Code, m.Title, t)))];

        // Ключи доступа сборки: коды модулей и права — всех модулей, включая выключенные, и ядра.
        var keys = all.Select(m => m.Code)
            .Concat(all.SelectMany(m => m.Permissions).Concat(corePermissions ?? []).Select(p => p.Code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();
        foreach (var entry in All)
        {
            broken.AddRange(entry.Table.Problems().Select(p => $"«{entry.Address}»: {p}"));

            // Ключа, которого нет в сборке, нет ни у кого: отбор с опечаткой в коде права не предлагался
            // бы НИКОМУ, и экран читался бы как «наводить нечего» (ревью PR #1240). Проверяется здесь,
            // а не в самом объявлении: какие права есть, знает только сборка.
            broken.AddRange((entry.Table?.Shortcuts ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s?.Requires) && !keys.Contains(s!.Requires!))
                .Select(s => $"«{entry.Address}»: готовый отбор «{s.Code}» открывается ключом «{s.Requires}», " +
                             "а такого нет ни среди модулей, ни среди прав"));
        }
        foreach (var twice in All.GroupBy(e => e.Address, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            broken.Add($"«{twice.Key}»: таблица объявлена дважды");

        if (broken.Count > 0)
            throw new InvalidOperationException(
                "Таблица модуля объявлена негодно: " + string.Join("; ", broken) + ".\n" +
                "Таблица без параметра доступа не регистрируется (ТЗ CORE-33, CORE-24.1): она отдавала " +
                "бы строки всем, кто вошёл. Отказ приходит при старте нарочно — забытое объявление " +
                "обязано остановить выпуск, а не открыться у заказчика.");

        _byAddress = All.ToDictionary(e => e.Address, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModuleTableEntry> All { get; }

    public ModuleTableEntry? Find(string address) => _byAddress.GetValueOrDefault(address);

    /// <summary>
    /// Зарегистрировал ли ВКЛЮЧЁННЫЙ модуль службы строк своих таблиц. Зовётся сразу за
    /// <c>RegisterServices</c> модуля — там, где видно и объявление, и контейнер.
    ///
    /// <para>Без проверки объявленная таблица без службы подняла бы приложение зелёным, а первый
    /// запрос к ней ответил бы «служба не зарегистрирована» — у пользователя, а не у того, кто собирал
    /// поставку.</para>
    /// </summary>
    public static void EnsureReaders(IAppModule module, IServiceCollection services)
    {
        // Таблицу без службы (Reader = null) здесь пропускаем: её назовёт каталог среди прочих ошибок
        // объявления — он собирает все разом, а этот отказ остановил бы старт раньше и без них.
        var missing = module.Tables
            .Where(t => t?.Reader is not null && !services.Any(d => d.ServiceType == t.Reader))
            .Select(t => $"«{ModuleTable.Address(module.Code, t.Code)}» ({t.Reader.Name})")
            .ToList();
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"Модуль «{module.Code}» объявил таблицы, но не зарегистрировал их службы строк в " +
            $"RegisterServices: {string.Join(", ", missing)}. Таблица открылась бы и отказала на " +
            "первом же чтении.");
    }
}
