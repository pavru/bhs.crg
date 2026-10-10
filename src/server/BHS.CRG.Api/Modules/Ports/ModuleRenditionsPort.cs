using BHS.CRG.Api.Renditions;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Читаемые образы для модулей (issue #1269) — переходник к службе ядра
/// (<see cref="RenditionStore" />).
///
/// <para>Порт отдельный, а не три метода у <see cref="IModuleBlobs" /> или
/// <see cref="IModuleRecognition" />: просмотру образ нужен без распознавания, а распознаванию — без
/// знания о том, где и как он хранится.</para>
/// </summary>
public sealed class ModuleRenditionsPort(RenditionStore store) : IModuleRenditions
{
    public async Task<ModuleRendition> EnsureAsync(string originalPath, CancellationToken ct = default) =>
        Shown(await store.EnsureAsync(originalPath, ct));

    public async Task<ModuleRendition?> FindAsync(string originalPath, CancellationToken ct = default) =>
        await store.FindAsync(originalPath, ct) is { } record ? Shown(record) : null;

    public async Task<ModuleRendition> RebuildAsync(string originalPath, CancellationToken ct = default) =>
        Shown(await store.RebuildAsync(originalPath, ct));

    private static ModuleRendition Shown(RenditionRecord record) => record.State switch
    {
        RenditionState.AsIs => new ModuleRendition.AsIs(record.OriginalBlobPath, record.Mime!),
        RenditionState.Built => new ModuleRendition.Built(
            record.ImageBlobPath!, record.Pages ?? 0, record.Notes, record.Converter, record.UpdatedAt),
        // Повтор помогает ровно тогда, когда так считает служба образов: правило одно, и живёт оно
        // у вида отказа, а не здесь.
        _ => new ModuleRendition.Refused(
            record.RefusalReason!,
            Enum.TryParse<RenditionRefusal>(record.RefusalKind, out var kind) && new Rendition.Refused(kind, "").RetryHelps),
    };
}
