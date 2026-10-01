using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Сторож объявлений системных наборов (ТЗ CORE-24.1, CORE-24.3, issue #965): набор без параметра
/// доступа и без текста границы выдачи не регистрируется — старт падает.
///
/// <para><b>Почему при старте, а не при первом чтении.</b> Реестр поставщиков создаётся на запрос, и
/// проверка в его конструкторе пришла бы пользователю пятисотым ответом из экрана наборов —
/// приложение при этом считалось бы поднявшимся. На этом уже наступали с реестром тэгов (ревью
/// PR #1012).</para>
///
/// <para><b>Почему здесь, в Api.</b> Заполненность объявления проверяет само Application
/// (<see cref="SystemDataProviderRegistry.EnsureDeclared" />) — ему для этого ничего не нужно. А вот
/// «такое право вообще объявлено» и «такой модуль в сборке есть» знают только здесь: справочник прав
/// и реестр модулей живут в Api.</para>
/// </summary>
/// <remarks>
/// Класс public, а не internal: правило годности ключа (<see cref="KeyIsKnown" />) проверяется
/// прогоном, а <c>InternalsVisibleTo</c> в решении не заведён ни для одной сборки.
/// </remarks>
public static class SystemDataSetDeclarations
{
    public static void Validate(IServiceProvider services)
    {
        var providers = services.GetRequiredService<SystemDataProviderRegistry>();
        providers.EnsureDeclared();

        var permissions = services.GetRequiredService<PermissionCatalog>();
        var modules = services.GetRequiredService<ModuleRegistry>();
        var known = new HashSet<string>(
            modules.Enabled.Concat(modules.Disabled).Select(m => m.Code).Append(
                SystemDataSetDeclaration.CoreModule),
            StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();
        foreach (var provider in providers.All)
        {
            var declaration = provider.Declaration;
            // Поставщик таблицы — один класс на все таблицы модулей: по имени класса не понять, какая
            // из них сломана, поэтому у него имя — адрес таблицы (ревью PR #1130).
            var name = provider is BHS.CRG.Api.Modules.Tables.ModuleTableDataProvider table
                ? $"таблица «{table.Address}»"
                : provider.GetType().Name;

            // Модуль сверяем со ВСЕЙ сборкой, включая выключенные (ModuleRegistry.Disabled): набор
            // выключенного модуля — штатное состояние, он отвечает «модуль не подключён» на чтении
            // (ТЗ CORE-24.3). А вот неизвестный код — опечатка, и она превратила бы набор в
            // нечитаемый навсегда, причём с той же формулировкой про неподключённый модуль.
            if (!known.Contains(declaration.Module))
                broken.Add($"{name}: модуля «{declaration.Module}» нет в сборке");

            // Ключ доступа — либо объявленное право, либо код своего модуля (у библиотеки документов
            // качества своего права нет, ТЗ AUTH-12.2). Право с опечаткой никому не выдано, то есть
            // набор закрыт для всех, включая администратора, — и выглядело бы это как «мне не дали
            // прав».
            //
            // ⚠️ Права ВЫКЛЮЧЕННОГО модуля в справочнике отсутствуют законно: PermissionCatalog
            // собирается из ядра и ВКЛЮЧЁННЫХ модулей. Поэтому ключ принимается и тогда, когда он
            // принадлежит модулю из сборки по префиксу — `id.document.read` при выключенном `id`.
            // Прежнее правило сверяло только полное совпадение с кодом модуля, и поставка без `id`
            // не поднималась вовсе вместо ожидаемого «модуль не подключён» на чтении (нашло ревью
            // PR #1057). Цена послабления названа вслух: опечатку в праве выключенного модуля
            // отличить не от чего — справочника его прав на этом экземпляре не существует.
            if (!KeyIsKnown(declaration.Requires, known, permissions))
                broken.Add($"{name}: ключа доступа «{declaration.Requires}» нет ни в справочнике прав, " +
                           "ни среди модулей сборки");
        }

        // Ключи КОЛОНОК таблиц модулей (G1b, issue #1089). Ключ самой таблицы проверен выше — у неё
        // свой поставщик набора. Ключ колонки с опечаткой никому не выдан, и колонка сумм пришла бы
        // «нет права на суммы» даже администратору.
        foreach (var entry in services.GetRequiredService<BHS.CRG.Modules.Tables.ModuleTableCatalog>().All)
        foreach (var column in entry.Table.Columns.Where(c => c.Requires is not null))
            if (!KeyIsKnown(column.Requires!, known, permissions))
                broken.Add($"таблица «{entry.Address}», колонка «{column.Key}»: ключа доступа " +
                           $"«{column.Requires}» нет ни в справочнике прав, ни среди модулей сборки");

        if (broken.Count == 0) return;

        throw new InvalidOperationException(
            "Системный набор ссылается на то, чего нет: " + string.Join("; ", broken) + ".\n" +
            "Набор с несуществующим ключом доступа не откроется никому, а с неизвестным модулем " +
            "будет вечно отвечать «модуль не подключён» — оба случая выглядят как отобранные права.");
    }

    /// <summary>
    /// Годен ли ключ доступа: объявленное право, код своего модуля либо право модуля из сборки
    /// (по префиксу до первой точки — <c>id.document.read</c> принадлежит модулю <c>id</c>).
    ///
    /// <para>Отдельной функцией, чтобы правило можно было проверить прогоном без поднятия хоста:
    /// самый важный его случай — ВЫКЛЮЧЕННЫЙ модуль, а хост прогона поднимается с включённым.</para>
    /// </summary>
    public static bool KeyIsKnown(string key, ISet<string> knownModules, PermissionCatalog permissions)
    {
        if (permissions.Declares(key)) return true;
        if (knownModules.Contains(key)) return true;

        var dot = key.IndexOf('.');
        return dot > 0 && knownModules.Contains(key[..dot]);
    }
}
