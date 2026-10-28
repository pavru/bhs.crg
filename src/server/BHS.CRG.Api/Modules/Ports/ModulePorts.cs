using BHS.CRG.Api.Renditions;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Порты ядра для модулей: одна регистрация на все переходники (задача M2 этапа 2, issue #1069,
/// ТЗ CORE-2, CORE-3, CORE-34).
///
/// <para><b>Зачем порты вообще.</b> Модулю разрешены только контракты ядра и домен
/// (<c>ModuleBoundaryTests</c>), а всё, чем система работает с данными, живёт в слоях, на которые
/// ссылаться нельзя: журнал действий, хранилище файлов, уведомления, очередь задач, счётчик прав,
/// справочники, охрана записи. Обойти это нельзя даже при желании — ссылка модуля на слой доступа к
/// данным роняет сторож. Значит, либо у модуля есть порты, либо у него есть СВОИ журнал, хранилище и
/// очередь — то есть второй продукт внутри первого.</para>
///
/// <para><b>Почему переходники живут здесь.</b> Это единственный проект, который видит и контракты
/// модулей, и слои приложения. Снизу вверх сослаться нельзя: любой проект, сославшийся на контракты
/// модулей, сам становится модулем по определению сторожа — а слой доступа к данным модулем быть не
/// может.</para>
///
/// <para>⚠️ Порт, у которого здесь нет строки, модуль получить не сможет: он попросит службу, которой
/// в контейнере нет, и отказ придёт первым обращением в бою, а не при старте. Против этого стоит
/// <c>ModulePortsTests.Каждый_порт_получается_из_приложения</c> — он перечисляет порты отражением и
/// требует, чтобы каждый разрешался. Новый порт без переходника роняет тест с его именем.</para>
///
/// <para>Область — <c>Scoped</c> у всех, и это не осторожность вообще, а следствие: журнал пишет
/// через контекст базы запроса, охрана читает справочники схемы, пользователь — принципала запроса.
/// Порт-одиночка, ухвативший их, отдал бы второму запросу данные первого.</para>
/// </summary>
public static class ModulePorts
{
    public static IServiceCollection AddModulePorts(this IServiceCollection services)
    {
        // Каталог действий журнала: ядро плюс объявления включённых модулей. Одиночка — состав
        // модулей за время работы не меняется, а объявления обязаны быть проверены ОДИН раз и при
        // старте (разрешается в StartupTasks).
        services.AddSingleton<Activity.ActivityActionCatalog>();
        services.AddSingleton<BHS.CRG.Application.Activity.IActivityActionLookup>(
            sp => sp.GetRequiredService<Activity.ActivityActionCatalog>());

        services.AddScoped<IModuleActivityLog, ModuleActivityLogPort>();
        // Файлы и их читаемые образы (issue #1269) — по ключу, коду модуля (ТЗ CORE-37.2): у каждого
        // включённого модуля свой экземпляр, и порт знает, от чьего имени читать. Образом владеет ядро,
        // модуль спрашивает его по пути оригинала. Без ключа этих портов в контейнере нет.
        foreach (var code in ModuleCodes(services))
        {
            services.AddKeyedScoped<IModuleBlobs>(code,
                (sp, _) => new ModuleBlobsPort(sp.GetRequiredService<IBlobStorage>(), code));
            services.AddKeyedScoped<IModuleRenditions>(code,
                (sp, _) => new ModuleRenditionsPort(sp.GetRequiredService<RenditionStore>(), code,
                    sp.GetRequiredService<ILogger<ModuleRenditionsPort>>()));
        }
        services.AddScoped<IModuleNotifications, ModuleNotificationsPort>();
        services.AddScoped<IModuleJobs, ModuleJobsPort>();
        services.AddScoped<IModuleUser, ModuleUserPort>();
        // Потребности модулей в справочниках ядра (issue #1101). Одиночка по той же причине, что
        // каталог действий: состав модулей не меняется, а объявления проверяются при старте.
        services.AddSingleton<ModuleNeeds>();
        services.AddScoped<IModuleCatalog, ModuleCatalogPort>();
        // Справочники ядра, которые модуль читает по смыслу, а не данными строкой (plan-03, CORE-20.4):
        // вид работы с единицей и признаками, единица, сотрудник.
        services.AddScoped<IModuleWorkTypes, ModuleWorkTypesPort>();
        services.AddScoped<IModuleUnits, ModuleUnitsPort>();
        services.AddScoped<IModuleEmployees, ModuleEmployeesPort>();
        // Позиции перечня работ — «найди или создай» по ключу (plan-04, CORE-12): заводит их только сервер.
        services.AddScoped<IModulePlanItems, ModulePlanItemsPort>();
        services.AddScoped<IModuleOwnCatalog, ModuleOwnCatalogPort>();
        // Заведение записи справочника ЯДРА модулем (issue #1077): только создание, только объявленные типы.
        services.AddScoped<IModuleCatalogIntake, ModuleCatalogIntakePort>();
        services.AddScoped<IModuleConstructions, ModuleConstructionsPort>();
        services.AddScoped<IModuleReferenceTargets, ModuleReferenceTargetsPort>();
        services.AddScoped<IModuleTypes, ModuleTypesPort>();
        services.AddScoped<IModuleWriteGuard, ModuleWriteGuardPort>();
        // Распознавание для модулей (issue #1077): профиль модуля, движки и запрос — ядра.
        services.AddScoped<IModuleRecognition, ModuleRecognitionPort>();
        // Куда ложатся поля профиля (issue #1483): объявленное модулем плюс поля, дописанные в его тип.
        services.AddScoped<IModuleRecognitionTargets, ModuleRecognitionTargetsPort>();
        // Границы закрытия периода — из службы ядра (ТЗ CORE-35). Сама служба регистрируется с
        // остальными службами ядра: она работает и при пустом составе модулей.
        services.AddScoped<IModulePeriods, ModulePeriodsPort>();

        // «Сегодня» в поясе компании. Часы — службой, а не DateTime.Now: границу суток иначе нечем
        // проверить. TryAdd — часы мог уже зарегистрировать сам ASP.NET.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IModuleClock, ModuleClockPort>();

        // Настройки модуля (M1, issue #1070). Scoped: значения читаются из базы один раз на запрос.
        services.AddScoped<IModuleSettings, ModuleSettingsPort>();
        // Какие ключи настроек экземпляра знает ЭТА сборка — ядро плюс модули. Спрашивает его
        // восстановление копии в инфраструктуре, которой о контрактах модулей знать нельзя.
        services.AddSingleton<BHS.CRG.Application.Settings.IAppSettingCatalog, AppSettingCatalog>();
        // Держат ли документы модулей валюту учёта (issue #1248). Нужен при любом составе модулей:
        // без модулей держать нечему, а настройка есть.
        services.AddScoped<BHS.CRG.Application.Settings.IAccountingCurrencyLock, AccountingCurrencyLock>();
        // Запись настроек модулей вместе с обработчиками их смены (E2, issue #1100): адрес настроек и
        // выравнивание при старте. При любом составе модулей: без обработчиков это просто запись.
        services.AddScoped<ModuleSettingWriter>();
        // Он же решает, может ли восстановление копии сменить настройку мимо обработчика.
        services.AddScoped<BHS.CRG.Application.Settings.IModuleSettingRestoreGuard>(
            sp => sp.GetRequiredService<ModuleSettingWriter>());

        // Кто выполняет фоновую работу модуля. Спрашивает её фоновый цикл в инфраструктуре — по
        // интерфейсу из слоя приложения, потому что о контрактах модулей ему знать нельзя.
        services.AddScoped<IModuleWorkRunner, ModuleWorkRunner>();

        // Разделы модулей для диалога закрытия периода (E1b). Нужен службе закрытия ядра при ЛЮБОМ
        // составе модулей, включая пустой: без модулей перечень пуст, а закрытие работает.
        services.AddScoped<BHS.CRG.Application.Periods.IClosingReports, ModuleClosingReports>();

        // ⚠️ IEnabledModules здесь НЕТ намеренно: его реализует сам реестр модулей, и регистрирует его
        // AddAppModules — вторая реализация означала бы второй источник истины о составе поставки.

        return services;
    }

    /// <summary>
    /// Коды включённых модулей — из реестра, который <c>AddAppModules</c> положил в контейнер. Порты
    /// по ключу регистрируются при сборке, а не лениво: модуль, попросивший порт со своим ключом, обязан
    /// получить его на старте, а не узнать о его отсутствии первым запросом.
    ///
    /// <para>Код «core» модулю запрещён здесь же: им назван владелец файлов ядра
    /// (<see cref="BlobOwner.Core" />), и такой модуль читал бы своим портом все вложения. Как и код,
    /// которого реестр файлов не примет владельцем (<see cref="BlobOwner.IsValid" />): иначе модуль
    /// поднимался бы зелёным и узнавал об этом первой загрузкой — после записи объекта в хранилище.</para>
    /// </summary>
    private static IReadOnlyList<string> ModuleCodes(IServiceCollection services)
    {
        var registry = services
                           .Where(d => !d.IsKeyedService && d.ServiceType == typeof(ModuleRegistry))
                           .Select(d => d.ImplementationInstance).OfType<ModuleRegistry>().SingleOrDefault()
                       ?? throw new InvalidOperationException(
                           "Порты модулей регистрируются после AddAppModules: без реестра модулей порту " +
                           "файлов не назвать, от чьего имени он читает.");
        if (registry.Codes.FirstOrDefault(c => c == BlobOwner.Core) is { } core)
            throw new InvalidOperationException(
                $"Модуль назван кодом «{core}», которым названы файлы ядра: его порт читал бы все вложения.");
        if (registry.Codes.FirstOrDefault(c => !BlobOwner.IsValid(c)) is { } bad)
            throw new InvalidOperationException(
                $"Код модуля «{bad}» не годится владельцем файла: пустой, «{BlobOwner.Any}» или длиннее " +
                $"{BlobOwner.MaxLength} символов.");
        return registry.Codes;
    }
}
