using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Каталог моделей для тестов: про зрение отвечает тем, что в него положили, про всё остальное —
/// «не проверено». Пустой (ничего не положили) значит «канарейка ничего не выяснила» — то состояние,
/// в котором распознавание обязано работать как прежде.
/// </summary>
public sealed class FakeCatalog : Dictionary<string, VisionStatus>, IRecognitionModelCatalog
{
    public List<string> Asked { get; } = [];

    public Task<IReadOnlyList<string>?> GetInstalledAsync(string engine, IntegrationEngine cfg, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    public Task<ModelStatus> GetStatusAsync(string engine, IntegrationEngine cfg, string model,
        bool probe = true, CancellationToken ct = default)
        => Task.FromResult(ModelStatus.Unknown);

    public Task<VisionStatus> GetVisionAsync(string engine, IntegrationEngine cfg, string model,
        bool probe = true, CancellationToken ct = default)
    {
        Asked.Add(engine);
        return Task.FromResult(TryGetValue(engine, out var v) ? v : VisionStatus.Unknown);
    }
}
