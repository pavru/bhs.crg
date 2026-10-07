using System.Reflection;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Settings;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Объявление настроек модуля (задача M1, issue #1070): негодное объявление роняет старт, значение
/// проверяет само объявление, а каждая заведённая настройка объявлена и кем-то читается.
/// </summary>
public class ModuleSettingCatalogTests
{
    private static NumberSetting Number(string key, decimal value = 1m, decimal min = 0m, decimal max = 10m) =>
        new(key, "Подпись", "Что меняет", value, min, max, Scale: 2);

    [Theory]
    [InlineData("probe.tolerance", "вида «модуль.объект.настройка»")]
    [InlineData("probe..tolerance", "вида «модуль.объект.настройка»")]
    [InlineData("other.sums.tolerance", "обязан начинаться с «probe.»")]
    public void Ключ_не_по_форме_или_с_чужим_префиксом_роняет_старт(string key, string reason)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => new ModuleSettingCatalog([new ProbeModule("probe", Number(key))]));
        Assert.Contains(reason, refusal.Message);
        Assert.Contains("probe", refusal.Message);
    }

    [Fact]
    public void Ключ_объявленный_дважды_роняет_старт()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new ModuleSettingCatalog(
            [new ProbeModule("probe", Number("probe.sums.tolerance"), Number("probe.sums.tolerance"))]));
        Assert.Contains("объявлен дважды", refusal.Message);
    }

    /// <summary>Умолчание вне собственных границ — настройка, которую нельзя сохранить как есть.</summary>
    [Fact]
    public void Умолчание_не_проходящее_собственную_проверку_роняет_старт()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new ModuleSettingCatalog(
            [new ProbeModule("probe", Number("probe.sums.tolerance", value: 50m, max: 10m))]));
        Assert.Contains("умолчание", refusal.Message);
    }

    [Fact]
    public void Настройка_без_подписи_или_без_следствия_роняет_старт()
    {
        var nameless = Number("probe.sums.tolerance") with { Title = " " };
        var silent = Number("probe.sums.tolerance") with { Effect = "" };

        Assert.Contains("нет подписи", Assert.Throws<InvalidOperationException>(
            () => new ModuleSettingCatalog([new ProbeModule("probe", nameless)])).Message);
        Assert.Contains("что настройка меняет", Assert.Throws<InvalidOperationException>(
            () => new ModuleSettingCatalog([new ProbeModule("probe", silent)])).Message);
    }

    /// <summary>
    /// Каталог собирают из ВСЕЙ сборки: ключ выключенного модуля обязан узнаваться при
    /// восстановлении копии. Объявление одного модуля другому не видно.
    /// </summary>
    [Fact]
    public void Каталог_находит_ключ_и_отдаёт_настройки_по_модулям()
    {
        var first = Number("probe.sums.tolerance");
        var catalog = new ModuleSettingCatalog([new ProbeModule("probe", first), new ProbeModule("empty")]);

        Assert.Same(first, catalog.Find("probe.sums.tolerance"));
        Assert.Null(catalog.Find("probe.sums.other"));
        Assert.Equal([first], catalog.Of("probe"));
        Assert.Empty(catalog.Of("empty"));
        Assert.Empty(catalog.Of("nope"));
        Assert.True(catalog.Declares(first));
        // Тот же ключ другим объектом — не объявление: читать можно только то, что вписано.
        Assert.False(catalog.Declares(first with { DefaultValue = 2m }));
    }

    [Fact]
    public void Число_читается_в_границах_а_негодное_сохранённое_уступает_умолчанию()
    {
        var setting = Number("probe.sums.tolerance", value: 1m, max: 10m);

        Assert.Equal(2.5m, setting.Read("2.50"));
        Assert.Equal(1m, setting.Read(null));
        // Значение, сохранённое прежней версией с другими границами, расчёты ронять не должно.
        Assert.Equal(1m, setting.Read("50"));
        Assert.Equal(1m, setting.Read("много"));

        Assert.Equal("2.50", setting.Normalize("2.5"));
        Assert.Equal("2,50", setting.Display("2.5"));
        Assert.Equal("2,50 ₽", (setting with { Unit = "₽" }).Display("2.5"));
        Assert.Equal("допустимо от 0,00 до 10,00", setting.Refuse("11"));
        Assert.Equal("1.00", setting.DefaultText);
        Assert.Null(setting.Refuse("0"));
        Assert.Null(setting.Refuse("10.00"));
        Assert.NotNull(setting.Refuse("1 000"));
        Assert.NotNull(setting.Refuse(""));
    }

    // ── Настоящие модули ──────────────────────────────────────────────────────

    private static readonly IAppModule[] Real = BHS.CRG.Api.Modules.DeliveredModules.All();

    /// <summary>
    /// Каждое поле-настройка модуля вписано в его <c>Settings</c>. Забытое читалось бы портом с
    /// отказом — но только на том пути, где его читают; здесь оно видно сразу и по имени.
    /// </summary>
    [Fact]
    public void Каждая_заведённая_настройка_объявлена_модулем()
    {
        var stray = Real.SelectMany(module => Fields(module)
                .Where(f => !module.Settings.Any(s => ReferenceEquals(s, f.Value)))
                .Select(f => $"{module.Code}: {f.Name}"))
            .ToList();

        Assert.True(stray.Count == 0,
            "Настройка заведена полем, но в Settings модуля не вписана:\n  " + string.Join("\n  ", stray) +
            "\n\nБез объявления её не видит администратор и не узнаёт резервная копия, а порт " +
            "IModuleSettings откажет при первом чтении.");
    }

    /// <summary>
    /// У каждой объявленной настройки есть потребитель: её поле читают где-то, кроме файла, в
    /// котором оно объявлено. Настройка без потребителя сохраняется и не действует — отказ,
    /// переодетый в удавшуюся запись (то самое, ради чего у расчёта баланса нет умолчания допуска).
    /// </summary>
    [Fact]
    public void У_каждой_объявленной_настройки_есть_потребитель()
    {
        var unread = new List<string>();
        foreach (var module in Real)
        {
            var project = Path.Combine(SolutionDir, module.GetType().Assembly.GetName().Name!);
            var sources = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(f => (File: f, Text: File.ReadAllText(f)))
                .ToList();

            foreach (var field in Fields(module))
            {
                var name = $"{field.Owner}.{field.Name}";
                var declaredIn = sources.Where(s => s.Text.Contains($"class {field.Owner}")).Select(s => s.File).ToHashSet();
                if (!sources.Any(s => !declaredIn.Contains(s.File) && Code(s.Text).Contains(name)))
                    unread.Add($"{module.Code}: {name}");
            }
        }

        Assert.True(unread.Count == 0,
            "Настройка объявлена, но её никто не читает:\n  " + string.Join("\n  ", unread) +
            "\n\nОна будет сохраняться и ни на что не влиять. Объявляйте настройку вместе с работой, " +
            "которая её читает.");
    }

    /// <summary>Статические поля-настройки сборки модуля: где объявлено, как названо, что лежит.</summary>
    private static IEnumerable<(string Owner, string Name, ModuleSetting Value)> Fields(IAppModule module) =>
        module.GetType().Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(ModuleSetting).IsAssignableFrom(f.FieldType))
                .Select(f => (t.Name, f.Name, (ModuleSetting)f.GetValue(null)!)));

    /// <summary>Исходник без комментариев: упоминание настройки в комментарии — не чтение.</summary>
    private static string Code(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает исходники и без них проверять нечего.");
    }

    private sealed class ProbeModule(string code, params ModuleSetting[] settings) : IAppModule
    {
        public string Code => code;
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/" + code];
        public IReadOnlyList<ModuleSetting> Settings => settings;

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
