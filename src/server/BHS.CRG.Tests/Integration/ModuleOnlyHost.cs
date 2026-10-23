using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост «только этот модуль»: <c>Modules__Enabled</c> из одного кода и своя база рядом с общей
/// тестовой. Общая основа <see cref="CostsOnlyHost" /> и <see cref="PlanOnlyHost" /> (ревью PR #1580):
/// они различались только кодом модуля и суффиксом базы, и правка того, чем держится такой хост,
/// иначе шла бы в каждую копию.
///
/// <para>⚠️ Своя база — не украшение. Состав системной роли приводится при старте к объявленному, а
/// прав выключенных модулей в каталоге нет, — на общей тестовой базе такой хост снял бы у ролей права
/// остальных модулей, и падали бы соседние классы.</para>
///
/// <para>Наследуется от <see cref="IntegrationTestFixture" />, чтобы не разойтись с ним в том, чем
/// тестовый хост вообще держится. Переопределения добавляются ПОСЛЕ базовых — слой конфигурации,
/// добавленный последним, выигрывает, и одного <c>UseSetting</c> тут не хватило бы: базовый класс
/// кладёт те же ключи ещё и слоем на <c>Build</c>.</para>
///
/// <para>Код и суффикс — абстрактные свойства, а не параметры конструктора: базовый класс читает
/// <see cref="HostConnectionString" /> раньше, чем отработал бы конструктор наследника.</para>
/// </summary>
public abstract class ModuleOnlyHost : IntegrationTestFixture
{
    /// <summary>Код единственного включённого модуля.</summary>
    protected abstract string ModuleCode { get; }

    /// <summary>Суффикс своей базы: «&lt;база прогона&gt;_&lt;суффикс&gt;» (<see cref="TestDatabases" />).</summary>
    protected abstract string DatabaseSuffix { get; }

    /// <summary>База хоста. Создаётся сама: приложение мигрирует схему при старте, а миграция создаёт базу.</summary>
    internal string ConnectionString => TestDatabases.ConnectionString(DatabaseSuffix);

    protected override string HostConnectionString => ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = ModuleCode,
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
