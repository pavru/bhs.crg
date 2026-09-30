using BHS.CRG.Application.Jobs;
using BHS.CRG.Modules.Ports;

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

        services.AddScoped<IModuleActivityLog, ModuleActivityLogPort>();
        services.AddScoped<IModuleBlobs, ModuleBlobsPort>();
        services.AddScoped<IModuleNotifications, ModuleNotificationsPort>();
        services.AddScoped<IModuleJobs, ModuleJobsPort>();
        services.AddScoped<IModuleUser, ModuleUserPort>();
        services.AddScoped<IModuleCatalog, ModuleCatalogPort>();
        services.AddScoped<IModuleConstructions, ModuleConstructionsPort>();
        services.AddScoped<IModuleTypes, ModuleTypesPort>();
        services.AddScoped<IModuleWriteGuard, ModuleWriteGuardPort>();

        // Закрытых периодов пока не бывает — служба закрытия приезжает задачей E1a (issue #1081).
        // Ответ «не закрыто ничего» — правда об этом экземпляре, а не заглушка; подробнее в
        // доккомментарии класса.
        services.AddScoped<IModulePeriods, NoClosedPeriods>();

        // Кто выполняет фоновую работу модуля. Спрашивает её фоновый цикл в инфраструктуре — по
        // интерфейсу из слоя приложения, потому что о контрактах модулей ему знать нельзя.
        services.AddScoped<IModuleWorkRunner, ModuleWorkRunner>();

        // ⚠️ IEnabledModules здесь НЕТ намеренно: его реализует сам реестр модулей, и регистрирует его
        // AddAppModules — вторая реализация означала бы второй источник истины о составе поставки.

        return services;
    }
}
