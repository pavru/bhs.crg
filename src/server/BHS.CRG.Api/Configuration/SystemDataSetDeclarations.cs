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
internal static class SystemDataSetDeclarations
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
            var name = provider.GetType().Name;

            // Модуль сверяем со ВСЕЙ сборкой, включая выключенные (ModuleRegistry.Disabled): набор
            // выключенного модуля — штатное состояние, он отвечает «модуль не подключён» на чтении
            // (ТЗ CORE-24.3). А вот неизвестный код — опечатка, и она превратила бы набор в
            // нечитаемый навсегда, причём с той же формулировкой про неподключённый модуль.
            if (!known.Contains(declaration.Module))
                broken.Add($"{name}: модуля «{declaration.Module}» нет в сборке");

            // Ключ доступа — либо объявленное право, либо код своего модуля (у библиотеки документов
            // качества своего права нет, ТЗ AUTH-12.2). Право с опечаткой никому не выдано, то есть
            // набор закрыт для всех, включая администратора, — и выглядело бы это как «мне не дали
            // прав». Право ВЫКЛЮЧЕННОГО модуля в справочнике отсутствует законно, поэтому для него
            // засчитывается совпадение с кодом модуля.
            if (!permissions.Declares(declaration.Requires)
                && !string.Equals(declaration.Requires, declaration.Module, StringComparison.OrdinalIgnoreCase)
                && !known.Contains(declaration.Requires))
                broken.Add($"{name}: ключа доступа «{declaration.Requires}» нет ни в справочнике прав, " +
                           "ни среди кодов модулей");
        }

        if (broken.Count == 0) return;

        throw new InvalidOperationException(
            "Системный набор ссылается на то, чего нет: " + string.Join("; ", broken) + ".\n" +
            "Набор с несуществующим ключом доступа не откроется никому, а с неизвестным модулем " +
            "будет вечно отвечать «модуль не подключён» — оба случая выглядят как отобранные права.");
    }
}
