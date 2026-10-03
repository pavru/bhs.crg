using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>Регистрация таблиц модулей (задача G1b, issue #1089).</summary>
public static class ModuleTableRegistration
{
    /// <summary>
    /// Служба таблиц и по поставщику системного набора на каждую объявленную таблицу — включая таблицы
    /// ВЫКЛЮЧЕННЫХ модулей: источник на такой таблице не исчезает, а отвечает «модуль не подключён»
    /// воротами набора (ТЗ CORE-24.3).
    ///
    /// <para>⚠️ Зовётся ПОСЛЕ <c>AddAppModules</c>: каталог таблиц собран там, при старте, и здесь
    /// берётся тем же экземпляром. Позвать раньше — отказ с понятной причиной, а не тихо пустой список
    /// поставщиков.</para>
    /// </summary>
    public static IServiceCollection AddModuleTables(this IServiceCollection services)
    {
        var catalog = services
            .Select(d => d.ServiceType == typeof(ModuleTableCatalog) ? d.ImplementationInstance : null)
            .OfType<ModuleTableCatalog>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                "Таблицы модулей регистрируются после модулей: каталога таблиц в контейнере ещё нет.");

        services.AddScoped<ModuleTableService>();
        foreach (var entry in catalog.All)
            services.AddScoped<ISystemDataProvider>(sp =>
                new ModuleTableDataProvider(entry, sp.GetRequiredService<ModuleTableService>()));
        return services;
    }
}
