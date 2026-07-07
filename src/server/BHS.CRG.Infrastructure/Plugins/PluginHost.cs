using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text.Json;
using BHS.CRG.Plugins;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Plugins;

public interface IPluginHost
{
    IReadOnlyList<IDataSourcePlugin> Plugins { get; }
    IDataSourcePlugin? GetById(string id);

    /// <summary>Однократный прогрев при старте: запрашивает схемы у HTTP-плагинов (GET /schemas),
    /// т.к. их <see cref="IDataSourcePlugin.ProvidedSchemas"/> синхронны и на момент конструирования пусты.</summary>
    Task WarmUpAsync(CancellationToken ct = default);
}

public class PluginHost : IPluginHost
{
    private readonly List<IDataSourcePlugin> _plugins = [];
    private readonly ILogger<PluginHost> _logger;

    public PluginHost(PluginHostOptions options, ILogger<PluginHost> logger)
    {
        _logger = logger;
        // Загружаем .NET-плагины из папки
        foreach (var dir in options.PluginDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                try
                {
                    var ctx = new AssemblyLoadContext(dll, isCollectible: true);
                    var asm = ctx.LoadFromAssemblyPath(dll);
                    var pluginTypes = asm.GetExportedTypes()
                        .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(IDataSourcePlugin)));

                    foreach (var type in pluginTypes)
                    {
                        if (Activator.CreateInstance(type) is IDataSourcePlugin plugin)
                        {
                            _plugins.Add(plugin);
                            logger.LogInformation("Loaded plugin {Id} from {Dll}", plugin.Id, dll);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load plugin from {Dll}", dll);
                }
            }
        }

        // HTTP-плагины
        foreach (var httpPlugin in options.HttpPlugins)
            _plugins.Add(new HttpDataSourcePlugin(httpPlugin));
    }

    public IReadOnlyList<IDataSourcePlugin> Plugins => _plugins.AsReadOnly();
    public IDataSourcePlugin? GetById(string id) => _plugins.FirstOrDefault(p => p.Id == id);

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        foreach (var http in _plugins.OfType<HttpDataSourcePlugin>())
            await http.FetchSchemasAsync(_logger, ct);
    }
}

public class PluginHostOptions
{
    public List<string> PluginDirectories { get; set; } = [];
    public List<HttpPluginConfig> HttpPlugins { get; set; } = [];
}

public class HttpPluginConfig
{
    public string Id { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string BaseUrl { get; set; } = default!;
}

/// <summary>Адаптер для HTTP-плагинов, реализующих REST-контракт IDataSourcePlugin (/search, /fetch, /schemas).</summary>
public class HttpDataSourcePlugin : IDataSourcePlugin
{
    private readonly HttpPluginConfig _config;
    private readonly HttpClient _http;

    public HttpDataSourcePlugin(HttpPluginConfig config)
        : this(config, new HttpClient { BaseAddress = new Uri(config.BaseUrl) }) { }

    /// <summary>Тестовый конструктор — инъекция HttpClient (с мок-хендлером).</summary>
    public HttpDataSourcePlugin(HttpPluginConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public EntitySchema[] ProvidedSchemas { get; private set; } = [];

    /// <summary>Однократный запрос схем плагина (GET /schemas). Best-effort: плагин недоступен →
    /// ProvidedSchemas остаётся пустым, генерацию/старт не роняем (тот же стиль, что best-effort blob-операции).</summary>
    public async Task FetchSchemasAsync(ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var schemas = await _http.GetFromJsonAsync<EntitySchema[]>("/schemas", ct);
            if (schemas is not null) ProvidedSchemas = schemas;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HTTP-плагин {Id}: не удалось получить схемы (GET /schemas)", _config.Id);
        }
    }

    public async Task<SearchResult> SearchAsync(string entityType, string query, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/search", new { entityType, query }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SearchResult>(ct) ?? new([]);
    }

    public async Task<JsonDocument> FetchAsync(string entityType, string externalId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/fetch", new { entityType, externalId }, ct);
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
