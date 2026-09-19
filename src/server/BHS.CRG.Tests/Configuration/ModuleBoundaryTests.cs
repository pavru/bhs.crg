using System.Text.RegularExpressions;
using BHS.CRG.Modules;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Границы модулей: ссылки идут в одну сторону — модуль знает ядро, ядро о модуле не знает
/// (issue #942, ТЗ CORE-2, CORE-3).
///
/// Тест написан против дрейфа, а не ради нынешнего состава, и это важно понимать буквально: модуль
/// сейчас один и живёт обёрткой, так что запрещать пока почти нечего. Но правило дешёво ровно до
/// того дня, когда появится второй модуль, — а в этот день оно уже не правило, а разбор чужого
/// кода. Ровно так было с воротами адресов: пока их не было, каждый новый адрес открывался любому
/// вошедшему, и никто этого не замечал.
///
/// Проверяются ИСХОДНИКИ (файлы проектов), а не метаданные сборки: компилятор выбрасывает ссылку,
/// которой код не пользуется, — то есть объявленная, но пока неиспользуемая ссылка на слой
/// инфраструктуры в метаданных не видна, а в файле проекта видна.
/// </summary>
public class ModuleBoundaryTests
{
    /// <summary>
    /// Проект контрактов ядра не ссылается ни на один наш проект.
    ///
    /// Это главное, что тест стережёт сегодня. Самая вероятная правка — «добавлю сюда ссылку на
    /// Application, там же лежит нужный DTO»: она выглядит безобидно, проходит ревью и открывает
    /// модулю весь слой, потому что ссылки транзитивны.
    /// </summary>
    [Fact]
    public void Contracts_project_references_nothing_of_ours()
    {
        var csproj = Path.Combine(SolutionDir, "BHS.CRG.Modules", "BHS.CRG.Modules.csproj");
        var text = File.ReadAllText(csproj);

        var refs = ReferencedProjects(text).Select(r => r.Replace('\\', '/')).ToList();

        Assert.True(refs.Count == 0,
            "Проект контрактов ядра ссылается на наши проекты: " + string.Join(", ", refs) + ".\n" +
            "Через контракты модуль получает ровно то, что ему положено; ссылка отсюда на любой наш\n" +
            "слой открывает модулю этот слой целиком — ссылки транзитивны. Нужный тип либо\n" +
            "объявляется здесь, либо не нужен модулю.");
    }

    /// <summary>
    /// Проект модуля ссылается только на контракты и домен. Ни инфраструктуры, ни приложения, ни
    /// другого модуля.
    ///
    /// Сегодня таких проектов нет — модуль исполнительной документации объявлен обёрткой внутри
    /// API и своего проекта не имеет. Тест от этого не бессмысленный, а ждущий: первый же проект,
    /// названный по соглашению, попадёт под правило в день появления, а не после разбора.
    /// </summary>
    [Fact]
    public void Module_projects_reference_only_contracts_and_domain()
    {
        string[] allowed = ["BHS.CRG.Modules", "BHS.CRG.Domain"];

        var offenders = new List<string>();
        foreach (var csproj in ModuleProjectFiles())
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            var text = File.ReadAllText(csproj);
            foreach (var reference in ReferencedProjects(text))
            {
                var referenced = Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'));
                if (!allowed.Contains(referenced))
                    offenders.Add($"{name} → {referenced}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Модуль ссылается на то, на что ему нельзя: " + string.Join(", ", offenders) + ".\n" +
            "Разрешены только контракты ядра и домен. Ссылка на инфраструктуру открывает модулю\n" +
            "слой доступа к данным целиком; ссылка на другой модуль сводит два модуля в один —\n" +
            "и первая же задача, где графику нужна выработка, а учёту нормы, делает их неразделимыми.\n" +
            "Обмен данными между модулями идёт через контракты ядра.");
    }

    /// <summary>
    /// Набор модулей читается из настройки, и пустое значение не означает ни «всё», ни «ничего».
    ///
    /// Пустая строка в <c>.env</c> — обычный след невычищенной правки. «Включить всё» открыло бы
    /// на экземпляре заказчика модули, за которые он не платил; «выключить всё» подняло бы
    /// приложение без единого раздела. Оба толкования тихие, поэтому берётся умолчание.
    /// </summary>
    [Theory]
    [InlineData(null, "id")]
    [InlineData("", "id")]
    [InlineData("   ", "id")]
    [InlineData("costs", "costs")]
    [InlineData("id,costs", "id|costs")]
    [InlineData("id, costs ; work", "id|costs|work")]
    [InlineData("id,ID,id", "id")]
    public void Enabled_codes_are_read_from_configuration(string? value, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = value })
            .Build();

        Assert.Equal(expected, string.Join("|", ModuleRegistry.ReadEnabledCodes(configuration)));
    }

    /// <summary>Массивом настройка читается так же: этим путём её задаёт конфигурация среды.</summary>
    [Fact]
    public void Enabled_codes_are_read_from_array_form()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Enabled:0"] = "id",
                ["Modules:Enabled:1"] = "costs",
            })
            .Build();

        Assert.Equal("id|costs", string.Join("|", ModuleRegistry.ReadEnabledCodes(configuration)));
    }

    /// <summary>
    /// Незнакомый код в настройке останавливает старт и называет, что именно не нашлось.
    ///
    /// Тихий пропуск здесь — тот же отказ, переодетый в результат: приложение поднялось, раздела
    /// нет, и разбираться с этим будут не как с опечаткой в <c>.env</c>, а как с поломкой.
    /// </summary>
    [Fact]
    public void Unknown_module_code_stops_startup()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "id,nosuch" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, new FakeModule("id")));

        Assert.Contains("nosuch", ex.Message);
    }

    /// <summary>
    /// Настройка, заданная сразу списком и строкой, останавливает старт.
    ///
    /// Приоритет провайдеров конфигурации здесь не спасает: значение ветки и её дети лежат в
    /// разных местах и друг друга не перекрывают, поэтому «список из appsettings и строка из .env»
    /// дали бы молчаливый выбор одной из записей — то есть экземпляр, поднявшийся без модуля,
    /// который заказала поставка.
    /// </summary>
    [Fact]
    public void Enabled_codes_refuse_two_notations_at_once()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Enabled:0"] = "id",
                ["Modules:Enabled"] = "costs",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ReadEnabledCodes(configuration));
        Assert.Contains("costs", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    /// <summary>
    /// Адреса модуля остаются в документе API по умолчанию.
    ///
    /// Группа модуля нужна ради ворот, но имя группы в ASP.NET — это имя ДОКУМЕНТА OpenAPI.
    /// Назвав группу кодом модуля, мы вынесли все его адреса в документ, которого никто не
    /// заводит: адреса отвечали 401, то есть работали, а из описания API исчезли (ревью #968,
    /// 201 путь и ни одного из модуля). Тест стережёт именно возврат этой правки.
    /// </summary>
    [Fact]
    public void Module_endpoints_stay_in_the_default_api_document()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Services.AddAppModules(builder.Configuration, new RouteModule());

        using var app = builder.Build();
        app.MapAppModules();

        // DataSources у WebApplication — явная реализация интерфейса, отсюда приведение.
        var probe = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/probe-модуля");

        Assert.Null(probe.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointGroupNameMetadata>());
    }

    /// <summary>Модуль с одним адресом — чтобы было что искать среди зарегистрированных.</summary>
    private sealed class RouteModule : IAppModule
    {
        public string Code => ModuleRegistry.DefaultCode;
        public string Title => "проба";
        public IReadOnlyList<string> Permissions => [];
        public void RegisterServices(
            Microsoft.Extensions.DependencyInjection.IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) =>
            Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(endpoints, "/probe-модуля", () => "ок");
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeModule(string code) : IAppModule
    {
        public string Code => code;
        public string Title => code;
        public IReadOnlyList<string> Permissions => [];
        public void RegisterServices(
            Microsoft.Extensions.DependencyInjection.IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Ссылки на проекты из файла проекта.
    ///
    /// Разбор в два шага — тег целиком, потом атрибут — потому что однострочный регекс требовал
    /// <c>Include</c> ПЕРВЫМ атрибутом и только в двойных кавычках, а
    /// <c>&lt;ProjectReference Condition="…" Include='…'&gt;</c> проходил бы мимо правила
    /// (поймано на ревью #968). Сторож, который обходится перестановкой атрибутов, — не сторож.
    /// </summary>
    private static IEnumerable<string> ReferencedProjects(string csproj)
    {
        foreach (Match tag in ProjectReferenceTag.Matches(csproj))
        {
            var include = IncludeAttribute.Match(tag.Value);
            if (include.Success)
                yield return (include.Groups[1].Success ? include.Groups[1] : include.Groups[2]).Value;
        }
    }

    private static readonly Regex ProjectReferenceTag = new(
        @"<ProjectReference\b[^>]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IncludeAttribute = new(
        @"\bInclude\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Проекты модулей по соглашению об именовании: <c>BHS.CRG.Modules.&lt;код&gt;</c>.
    ///
    /// Отбор по префиксу с точкой, а не шаблоном каталога: шаблон <c>BHS.CRG.Modules.*</c> в .NET
    /// захватывает и сам <c>BHS.CRG.Modules</c> (наследие DOS-семантики «имя.*»), и проект
    /// контрактов попадал бы под правило модулей. Правило у него другое и строже — см. первый тест.
    /// </summary>
    private static IEnumerable<string> ModuleProjectFiles() =>
        Directory.EnumerateDirectories(SolutionDir)
            .Where(d => Path.GetFileName(d).StartsWith("BHS.CRG.Modules.", StringComparison.Ordinal))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.TopDirectoryOnly));

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает файлы проектов и без них проверять нечего.");
    }
}
