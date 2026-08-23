using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Отбор движков и предполётная проверка (issue #801). Правило одно на цепочку и на проверку перед
/// постановкой задачи — разъехавшись, они дали бы задачу, которую разрешили поставить и тут же
/// отказались выполнять.
/// </summary>
public class RecognitionEngineSelectorTests
{
    private static RecognitionEngineSelector Selector(IntegrationSettingsModel settings, FakeCatalog catalog,
        params string[] engines)
        => new(engines.Select(IRecognizerEngine (n) => new FakeEngine(n)).ToArray(),
               new FakeSettings(settings), catalog, NullLogger<RecognitionEngineSelector>.Instance);

    private static IntegrationSettingsModel Settings(params (string Name, IntegrationEngine Cfg)[] engines)
    {
        var m = new IntegrationSettingsModel { RecognitionOrder = engines.Select(e => e.Name).ToList() };
        foreach (var (name, cfg) in engines) m.Recognition[name] = cfg;
        return m;
    }

    private static IntegrationEngine Ready(string model = "qwen2.5vl:7b")
        => new() { Enabled = true, Model = model, ApiKey = "k" };

    [Fact]
    public async Task BlindEngineIsSkipped()
    {
        var catalog = new FakeCatalog { ["Ollama"] = new VisionStatus(VisionState.Blind) };
        var selection = await Selector(Settings(("Gemini", Ready()), ("Ollama", Ready())), catalog, "Gemini", "Ollama")
            .SelectAsync(ct: CancellationToken.None);

        Assert.Equal(["Gemini"], selection.Ordered.Select(e => e.Name));
        Assert.Equal("Ollama", Assert.Single(selection.Blind).Engine);
    }

    [Fact]
    public async Task UnknownVisionDoesNotBlock()
    {
        // «Не проверено» — это молчание, а не приговор: остановленная Ollama и таймаут канарейки не
        // повод отключать распознавание, которое работает.
        var selection = await Selector(Settings(("Ollama", Ready())), new FakeCatalog(), "Ollama")
            .SelectAsync(ct: CancellationToken.None);

        Assert.Equal(["Ollama"], selection.Ordered.Select(e => e.Name));
        Assert.Empty(selection.Blind);
    }

    [Fact]
    public async Task UnconfiguredEngineIsNotAskedAboutVision()
    {
        // У движка без ключа своя претензия, и канарейку ему слать незачем — цепочка его не берёт.
        var catalog = new FakeCatalog();
        var settings = Settings(("Gemini", new IntegrationEngine { Enabled = true }));
        var selection = await Selector(settings, catalog, "Gemini").SelectAsync(ct: CancellationToken.None);

        Assert.Empty(selection.Ordered);
        Assert.Empty(selection.Blind);
        Assert.Empty(catalog.Asked);
    }

    [Fact]
    public async Task Preflight_SaysBlind_NotJustUnavailable()
    {
        // Разные коды нужны потому, что чинится это по-разному: «не настроено» — галкой и ключом,
        // слепота — сменой модели. Совет «проверьте настройки» отправил бы человека искать то, что
        // и так на месте.
        var catalog = new FakeCatalog { ["Ollama"] = new VisionStatus(VisionState.Blind) };
        var preflight = new RecognitionPreflight(Selector(Settings(("Ollama", Ready("gemma4:latest"))), catalog, "Ollama"));

        var block = await preflight.CheckAsync(CancellationToken.None);

        Assert.Equal(RecognitionBlock.Blind, block!.Code);
        Assert.Contains("не принимает изображения", block.Message);
    }

    [Fact]
    public async Task Preflight_PassesWhenAnotherEngineIsSighted()
    {
        // Слепой движок сам по себе запуск не запрещает: работать есть кому.
        var catalog = new FakeCatalog
        {
            ["Ollama"] = new VisionStatus(VisionState.Blind),
            ["Gemini"] = VisionStatus.Unknown,
        };
        var preflight = new RecognitionPreflight(
            Selector(Settings(("Ollama", Ready("gemma4:latest")), ("Gemini", Ready())), catalog, "Ollama", "Gemini"));

        Assert.Null(await preflight.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preflight_NoEngineAtAll()
    {
        var preflight = new RecognitionPreflight(Selector(Settings(), new FakeCatalog()));
        var block = await preflight.CheckAsync(CancellationToken.None);

        Assert.Equal(RecognitionBlock.NoEngine, block!.Code);
    }

    private sealed class FakeEngine(string name) : IRecognizerEngine
    {
        public string Name => name;

        public Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Отбор движков не должен ничего распознавать.");
    }

    private sealed class FakeSettings(IntegrationSettingsModel model) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default) => Task.FromResult(model);
        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveUpdatesAsync(UpdateCheckSettings u, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupScheduleAsync(BackupScheduleSettings b, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveGithubAsync(GithubSettings g, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

}
