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
    /// <remarks>
    /// Записанный образ — одним запросом вместе с профилем (<see cref="RenditionStore.FindShownAsync" />):
    /// это путь каждого открытия счёта и каждого опроса его состояния.
    /// </remarks>
    public async Task<ModuleRendition> EnsureAsync(string originalPath, CancellationToken ct = default)
    {
        if (await store.FindShownAsync(originalPath, ct) is { } known) return Shown(known.Record, known.Profile);
        return await BuiltNowAsync(await store.EnsureAsync(originalPath, ct), ct);
    }

    public async Task<ModuleRendition?> FindAsync(string originalPath, CancellationToken ct = default) =>
        await store.FindShownAsync(originalPath, ct) is { } known ? Shown(known.Record, known.Profile) : null;

    public async Task<ModuleRendition> RebuildAsync(
        string originalPath, Guid? layoutProfileId = null, CancellationToken ct = default) =>
        await BuiltNowAsync(await store.RebuildAsync(originalPath, layoutProfileId, ct), ct);

    /// <summary>Только что построенный образ: профиль — отдельным вопросом, построение и так дорогое.</summary>
    private async Task<ModuleRendition> BuiltNowAsync(RenditionRecord record, CancellationToken ct) =>
        Shown(record, record.LayoutProfileId is { } id ? await store.ProfileOfAsync(id, ct) : null);

    private ModuleRendition Shown(RenditionRecord record, RenditionLayoutProfile? profile) => record.State switch
    {
        RenditionState.AsIs => new ModuleRendition.AsIs(record.OriginalBlobPath, record.Mime!),
        RenditionState.Built => new ModuleRendition.Built(
            record.ImageBlobPath!, record.Pages ?? 0, record.Notes, record.Converter, record.UpdatedAt,
            Layout(record, profile)),
        _ => new ModuleRendition.Refused(record.RefusalReason!, RetryHelps(record.RefusalKind),
            OtherKind: record.RefusalKind == nameof(RenditionRefusal.WrongFormat),
            AboutFile: Enum.TryParse<RenditionRefusal>(record.RefusalKind, out var kind) && kind.Remembered()),
    };

    /// <summary>
    /// Состояние раскладки — при чтении, а не из записи (issue #1454): «профиль изменён» и «профиль
    /// удалён» — это о профиле сейчас, и записанное однажды устарело бы с первой же его правкой.
    /// Переименование состояния не меняет: отпечаток — параметры без имени.
    ///
    /// <para>«До профилей» — и вид без исхода раскладки (до #1453), и вписанный без профиля: его вписала
    /// константа #1453, профилей тогда не было, и перестроение применит нынешний профиль (ревью PR #1472).</para>
    /// </summary>
    private static ModuleRenditionLayout Layout(RenditionRecord record, RenditionLayoutProfile? profile)
    {
        var outcome = record.Layout switch
        {
            null => ModuleLayoutOutcome.BeforeProfiles,
            RenditionLayout.NotBook => ModuleLayoutOutcome.NotBook,
            RenditionLayout.BookOwn => ModuleLayoutOutcome.BookOwn,
            RenditionLayout.Applied when record.LayoutProfileId is null => ModuleLayoutOutcome.BeforeProfiles,
            RenditionLayout.Applied => ModuleLayoutOutcome.Applied,
            _ => ModuleLayoutOutcome.Skipped,
        };
        if (record.LayoutProfileId is not { } id) return new(outcome, null, null, null);

        if (profile is null) return new(outcome, id, null, ModuleLayoutProfileState.Deleted);
        return new(outcome, id, profile.Name,
            profile.Stamp == record.LayoutStamp ? ModuleLayoutProfileState.Current : ModuleLayoutProfileState.Changed);
    }

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
