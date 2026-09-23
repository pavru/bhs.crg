using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Schema;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Реестр функциональных тэгов как ТОЧКА РАСШИРЕНИЯ ядра (ТЗ TYPE-22, issue #959): ядро объявляет
/// свои тэги, модуль — свои, выключенный модуль — никаких.
/// </summary>
public class TagCatalogTests
{
    /// <summary>Модуль с объявленными тэгами — минимальный, всё остальное ему для реестра не нужно.</summary>
    private sealed class TaggingModule(string code, params ModuleTag[] tags) : IAppModule
    {
        public string Code => code;
        public string Title => code;
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/" + code];
        public IReadOnlyList<ModuleTag> Tags => tags;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    private static ModuleTag Tag(string code) =>
        new(code, code, "описание", ModuleTagScope.Field, ["string"]);

    /// <summary>
    /// Собирает реестр ровно так, как это делает приложение, — через <see cref="ModuleTagCollector" />.
    /// Свой перевод в тесте проверял бы свой же перевод, а не тот, что работает в запуске.
    /// </summary>
    private static TagCatalog Build(IAppModule[] enabled, IAppModule[]? disabled = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleRegistry(enabled, disabled ?? []));
        services.AddTagCatalog();
        return services.BuildServiceProvider().GetRequiredService<TagCatalog>();
    }

    [Fact]
    public void Тэги_ядра_есть_всегда()
    {
        var catalog = Build([]);
        Assert.All(TagRegistry.Core, t => Assert.NotNull(catalog.Find(t.Code)));
    }

    [Fact]
    public void Тэг_включённого_модуля_предлагается_и_помнит_владельца()
    {
        var catalog = Build([new TaggingModule("work", Tag("work.shift"))]);

        var found = catalog.Find("work.shift");
        Assert.NotNull(found);
        Assert.Equal("work", found.Owner);
    }

    [Fact]
    public void Тэг_выключенного_модуля_не_предлагается()
    {
        // Модуль лежит в списке ВЫКЛЮЧЕННЫХ — то есть он есть в сборке, объявления у него на месте,
        // и спросить их можно. Их не спрашивают (ТЗ TYPE-22): предложить метку, которую на этом
        // экземпляре некому прочитать, значит обещать поведение, которого нет.
        //
        // ⚠️ Модуль обязан быть именно в «выключенных», а не отсутствовать вовсе: с пустым списком
        // проверка зеленела бы и тогда, когда отсев сломан, — потому что спрашивать было бы некого.
        var off = new TaggingModule("work", Tag("work.shift"));
        var catalog = Build([], [off]);

        Assert.Null(catalog.Find("work.shift"));
    }

    [Fact]
    public void Два_объявления_одного_кода_останавливают_сборку()
    {
        // Не «побеждает первый» и не «побеждает последний»: у двух объявлений разные подписи,
        // области и кратность, и молчаливый выбор решал бы за администратора, что он увидит в
        // редакторе, а за сервер — что тот проверит при сохранении.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build([new TaggingModule("work", Tag("shared.code")),
                   new TaggingModule("costs", Tag("shared.code"))]));

        Assert.Contains("shared.code", ex.Message);
        Assert.Contains("work", ex.Message);
        Assert.Contains("costs", ex.Message);
    }

    [Fact]
    public void Модуль_не_может_переобъявить_тэг_ядра()
    {
        var core = TagRegistry.Core[0].Code;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build([new TaggingModule("work", Tag(core))]));

        Assert.Contains(core, ex.Message);
    }

    [Fact]
    public void У_каждого_тэга_ядра_объявлен_владелец()
    {
        // Тэг без владельца невозможно ни выключить, ни объяснить: первым же вопросом о нём будет
        // «а кто это читает?». Пустая строка проходит мимо типа, поэтому её и проверяем.
        Assert.All(TagRegistry.Core, t =>
            Assert.False(string.IsNullOrWhiteSpace(t.Owner), $"тэг «{t.Code}» без владельца"));
    }

    /// <summary>
    /// Зеркало уровней тэга (та же идиома, что у <c>ModuleSchemaLevel</c>): у сборки контрактов
    /// модулей НОЛЬ ссылок на проекты, поэтому доменный перечень туда не дотянуть — и состав
    /// приходится сверять сторожем.
    ///
    /// <b>Чем ломается.</b> Добавить уровень в один перечень и забыть про второй: модуль объявит
    /// тэг уровня, которого в ядре нет, и узнается это при первом обращении к реестру.
    /// </summary>
    [Fact]
    public void Зеркало_уровней_тэга_совпадает_с_доменным()
    {
        Assert.Equal(
            Enum.GetNames<TagScope>().Order(StringComparer.Ordinal),
            Enum.GetNames<ModuleTagScope>().Order(StringComparer.Ordinal));
    }
}
