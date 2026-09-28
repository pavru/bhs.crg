using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Объявление схемы модуля: что считается годным и что останавливает старт (задача A2a этапа 2,
/// issue #1072, ТЗ CORE-4).
///
/// <para>Проверяется без базы: всё это решается на сборке приложения, до первого соединения. Живая
/// половина — что схема действительно создаётся, а данные не теряются — в
/// <c>ModuleSchemaTests</c>.</para>
/// </summary>
public class ModuleSchemaDeclarationTests
{
    /// <summary>
    /// Негодное имя схемы или негодный контекст — отказ, называющий причину.
    ///
    /// Каждая строка здесь — своя тихая поломка, а не вариация одной: схема ядра дописала бы модульные
    /// строки в историю миграций ядра, имя в другом регистре дало бы ДВЕ схемы (запрос без кавычек
    /// уходит не туда), чужое имя — схему, про которую в базе не ответить, чья она.
    /// </summary>
    [Theory]
    [InlineData("", "costs", "пусто")]
    [InlineData("   ", "costs", "пусто")]
    [InlineData("public", "costs", "схема ядра")]
    [InlineData("PUBLIC", "costs", "схема ядра")]
    [InlineData("Costs", "costs", "строчных латинских")]
    [InlineData("costs-2", "costs", "строчных латинских")]
    [InlineData("счета", "costs", "строчных латинских")]
    [InlineData("money", "costs", "названа не кодом модуля")]
    [InlineData("costsx", "costs", "названа не кодом модуля")]
    public void Bad_schema_name_is_refused(string name, string code, string expected)
    {
        var problem = new ModuleSchema(name, typeof(ProbeContext)).Validate(code);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    /// <summary>Своё имя и имя с приставкой через подчёркивание — годны.</summary>
    [Theory]
    [InlineData("costs")]
    [InlineData("costs_archive")]
    public void Own_schema_name_is_accepted(string name)
        => Assert.Null(new ModuleSchema(name, typeof(ProbeContext)).Validate("costs"));

    /// <summary>
    /// Контекст, не наследующий базовый, — отказ: без него у модуля нет ни схемы по умолчанию (первая
    /// таблица уехала бы в схему ядра), ни защиты дописываемых наборов от правки.
    /// </summary>
    [Fact]
    public void Context_outside_the_module_base_is_refused()
    {
        var problem = new ModuleSchema("costs", typeof(OutsiderContext)).Validate("costs");

        Assert.NotNull(problem);
        Assert.Contains(nameof(ModuleDbContext), problem);
    }

    /// <summary>
    /// Схема объявлена, а контекст модуль не зарегистрировал — старт останавливается.
    ///
    /// Иначе ядро мигрировало бы схему, работать с которой некому: приложение поднялось бы, а отказ
    /// «служба не зарегистрирована» пришёл бы первому пользователю модуля.
    /// </summary>
    [Fact]
    public void Declared_schema_without_a_registered_context_stops_startup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, new ForgetfulModule()));

        Assert.Contains("не зарегистрировал", ex.Message);
        Assert.Contains(nameof(ProbeContext), ex.Message);
    }

    /// <summary>
    /// Контекст зарегистрирован, а схема не объявлена — тоже отказ, и это самая тихая из двух
    /// половин: ядро о таком контексте не знает, миграций ему не будет никогда. У разработчика всё
    /// работает — таблицы он создал сам; у заказчика их не будет вовсе.
    /// </summary>
    [Fact]
    public void Registered_context_without_a_declaration_stops_startup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, new SilentModule()));

        Assert.Contains("не объявил", ex.Message);
        Assert.Contains(nameof(ProbeContext), ex.Message);
    }

    /// <summary>Объявление и регистрация сошлись — сборка проходит, контекст в контейнере.</summary>
    [Fact]
    public void Matching_declaration_and_registration_pass()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        services.AddAppModules(configuration, new ProbeModule());

        Assert.Contains(services, d => d.ServiceType == typeof(ProbeContext));
    }

    /// <summary>
    /// Контекст, зарегистрированный ФАБРИКОЙ (<c>AddDbContextFactory</c>), считается
    /// зарегистрированным.
    ///
    /// <para>Фабрика — обычный приём EF там, где области запроса нет вовсе: в фоновой работе модуля
    /// (<c>IModuleJobHandler</c>). Прежняя редакция опознавала только <c>AddDbContext</c>, и модуль с
    /// фабрикой получал при старте отказ «не зарегистрировал контекст» за правильно написанный код —
    /// то есть отказ обвинял автора в том, чего он не делал (ревью PR #1107).</para>
    /// </summary>
    [Fact]
    public void Context_registered_by_a_factory_counts_as_registered()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        services.AddAppModules(configuration, new FactoryModule());

        Assert.Contains(services, d => d.ServiceType == typeof(IDbContextFactory<ProbeContext>));
        // Самого контекста в контейнере нет: сверка опознала его именно по фабрике.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ProbeContext));
    }

    /// <summary>
    /// Фабрика контекста, которого модуль не объявил, — тот же отказ, что и у самого контекста:
    /// объявления нет, значит миграций не будет.
    /// </summary>
    [Fact]
    public void Factory_of_an_undeclared_context_stops_startup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, new SilentFactoryModule()));

        Assert.Contains("не объявил", ex.Message);
        Assert.Contains(nameof(ProbeContext), ex.Message);
    }

    /// <summary>
    /// Модуль, который в своих регистрациях ЧТО-ТО УБРАЛ, проходит сверку.
    ///
    /// <para>Окно «что зарегистрировал этот модуль» задавалось числом служб до вызова, а
    /// <c>RemoveAll</c>/<c>Replace</c> сдвигает индексы: удалив чужой дескриптор, модуль получал отказ
    /// «не зарегистрировал контекст», хотя контекст зарегистрировал (ревью PR #1107). Теперь окно —
    /// разница наборов, и позиция ни на что не влияет.</para>
    /// </summary>
    [Fact]
    public void Module_that_removes_a_registration_still_passes()
    {
        var services = new ServiceCollection();
        // Убираемых больше, чем модуль добавит: под прежней границей-индексом окно становилось пустым
        // при любом порядке регистраций EF, а не через раз.
        for (var i = 0; i < 20; i++) services.AddSingleton("чужая служба " + i);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        services.AddAppModules(configuration, new TidyingModule());

        Assert.Contains(services, d => d.ServiceType == typeof(ProbeContext));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(string));
    }

    /// <summary>
    /// Контекст соседнего модуля своим не считается: сверка смотрит на то, что зарегистрировал ИМЕННО
    /// этот модуль.
    ///
    /// Без этого отбора порядок модулей решал бы, проходит ли сборка: объявивший схему вторым нашёл бы
    /// «свой» контекст среди чужих регистраций.
    /// </summary>
    [Fact]
    public void Neighbours_context_does_not_count_as_ours()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe,late" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, new ProbeModule(), new LateModule()));

        Assert.Contains("«late»", ex.Message);
        Assert.Contains("не зарегистрировал", ex.Message);
    }

    /// <summary>Модуль-обёртка без своих таблиц схемы не объявляет — и это норма, а не пропуск.</summary>
    [Fact]
    public void Module_without_data_declares_no_schema()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "bare" })
            .Build();

        services.AddAppModules(configuration, new BareModule());

        Assert.DoesNotContain(services, d => typeof(ModuleDbContext).IsAssignableFrom(d.ServiceType));
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => "probe";
    }

    private sealed class OutsiderContext(DbContextOptions<OutsiderContext> options) : DbContext(options);

    /// <summary>Модуль, объявивший схему и зарегистрировавший контекст, — как положено.</summary>
    private sealed class ProbeModule : BareModule
    {
        public override string Code => "probe";
        public override ModuleSchema? Schema => new("probe", typeof(ProbeContext));

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddDbContext<ProbeContext>(o => o.UseNpgsql("Host=нет;Database=нет"));
    }

    /// <summary>
    /// Регистрирует ТОЛЬКО фабрику контекста, своими руками.
    ///
    /// ⚠️ Именно руками, а не <c>AddDbContextFactory</c>/<c>AddPooledDbContextFactory</c>: оба
    /// помощника EF заводят рядом и сам контекст (проверено прогоном), то есть прежнюю сверку они
    /// прошли бы. Отказ доставался бы модулю, который завёл фабрику сам — например под фоновую работу,
    /// где области запроса нет вовсе, — и обвинял бы его в том, чего он не делал.
    /// </summary>
    private sealed class FactoryModule : BareModule
    {
        public override string Code => "probe";
        public override ModuleSchema? Schema => new("probe", typeof(ProbeContext));

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<IDbContextFactory<ProbeContext>>(new ProbeContextFactory());
    }

    /// <summary>Фабрику зарегистрировал, схему объявить забыл.</summary>
    private sealed class SilentFactoryModule : BareModule
    {
        public override string Code => "probe";

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<IDbContextFactory<ProbeContext>>(new ProbeContextFactory());
    }

    private sealed class ProbeContextFactory : IDbContextFactory<ProbeContext>
    {
        public ProbeContext CreateDbContext() => new(
            new DbContextOptionsBuilder<ProbeContext>().UseNpgsql("Host=нет;Database=нет").Options);
    }

    /// <summary>Убирает чужую регистрацию и заводит свой контекст — так делает тестовый хост.</summary>
    private sealed class TidyingModule : BareModule
    {
        public override string Code => "probe";
        public override ModuleSchema? Schema => new("probe", typeof(ProbeContext));

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.RemoveAll<string>();
            services.AddDbContext<ProbeContext>(o => o.UseNpgsql("Host=нет;Database=нет"));
        }
    }

    /// <summary>Объявил схему, а контекст регистрировать забыл.</summary>
    private sealed class ForgetfulModule : BareModule
    {
        public override string Code => "probe";
        public override ModuleSchema? Schema => new("probe", typeof(ProbeContext));
    }

    /// <summary>Контекст зарегистрировал, объявить забыл.</summary>
    private sealed class SilentModule : BareModule
    {
        public override string Code => "probe";

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddDbContext<ProbeContext>(o => o.UseNpgsql("Host=нет;Database=нет"));
    }

    /// <summary>Объявляет ЧУЖОЙ контекст — тот, что зарегистрировал сосед раньше него.</summary>
    private sealed class LateModule : BareModule
    {
        public override string Code => "late";
        public override ModuleSchema? Schema => new("late", typeof(ProbeContext));
    }

    private class BareModule : IAppModule
    {
        public virtual string Code => "bare";
        public string Title => Code;
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/" + Code];
        public virtual ModuleSchema? Schema => null;
        public virtual void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
