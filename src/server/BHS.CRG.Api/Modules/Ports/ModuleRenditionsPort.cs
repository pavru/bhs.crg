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
public sealed class ModuleRenditionsPort(RenditionStore store, ILogger<ModuleRenditionsPort> log) : IModuleRenditions
{
    public async Task<ModuleRendition> EnsureAsync(string originalPath, CancellationToken ct = default) =>
        Shown(await store.EnsureAsync(originalPath, ct));

    public async Task<ModuleRendition?> FindAsync(string originalPath, CancellationToken ct = default) =>
        await store.FindAsync(originalPath, ct) is { } record ? Shown(record) : null;

    public async Task<ModuleRendition> RebuildAsync(string originalPath, CancellationToken ct = default) =>
        Shown(await store.RebuildAsync(originalPath, ct));

    private ModuleRendition Shown(RenditionRecord record) => record.State switch
    {
        RenditionState.AsIs => new ModuleRendition.AsIs(record.OriginalBlobPath, record.Mime!),
        RenditionState.Built => new ModuleRendition.Built(
            record.ImageBlobPath!, record.Pages ?? 0, record.Notes, record.Converter, record.UpdatedAt),
        _ => new ModuleRendition.Refused(record.RefusalReason!, RetryHelps(record.RefusalKind),
            OtherKind: record.RefusalKind == nameof(RenditionRefusal.WrongFormat),
            AboutFile: Enum.TryParse<RenditionRefusal>(record.RefusalKind, out var kind) && kind.Remembered()),
    };

    /// <summary>
    /// Поможет ли повтор — решает служба образов по виду отказа: правило одно, и живёт оно там.
    /// Вид в записи лежит именем; имя, которого служба не знает, значит, что запись сделана другой
    /// версией. Повтор тогда не обещаем, но и молча не проходим.
    /// </summary>
    private bool RetryHelps(string? kind)
    {
        if (Enum.TryParse<RenditionRefusal>(kind, out var known)) return known.RetryHelps();
        log.LogWarning("В записи читаемого образа неизвестный вид отказа: {Kind}", kind);
        return false;
    }
}
