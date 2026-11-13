using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Подготовка фото для модулей (work-05, ТЗ WORK-33, WORK-55, CORE-34) — переходник к
/// <see cref="PhotoPreparer" />: контракты модулей инфраструктуре недоступны, а инфраструктура — модулю.
///
/// <para>В хранилище порт не пишет и ключа модуля поэтому не требует: готовые байты модуль кладёт своим
/// портом файлов. Отказ лимита так не оставляет ни объекта, ни записи в реестре блобов.</para>
///
/// <para><b>Пояс времени съёмки.</b> EXIF пишет время так, как его показывали часы камеры, — без пояса.
/// Пояс записан, только если камера знает тег <c>OffsetTimeOriginal</c>; тогда берётся он. Иначе — пояс
/// компании (<see cref="IAppSettingsStore.GetCompanyTimeZoneAsync" />): тот же, которым система считает
/// «сегодня», и снимок со стройки в нём ближе к правде, чем в поясе сервера.</para>
/// </summary>
public sealed class ModulePhotosPort(IAppSettingsStore settings) : IModulePhotos
{
    public async Task<PhotoResult> PrepareAsync(Stream content, PhotoLimits limits, CancellationToken ct = default)
    {
        var prepared = await PhotoPreparer.PrepareAsync(content,
            new PhotoPreparerLimits(limits.MaxBytes, limits.MaxSide, limits.PreviewSide, limits.MaxPixels), ct);

        return prepared switch
        {
            PhotoPrepared p => new PreparedPhoto(p.Image, p.Preview, p.Width, p.Height,
                p.Geo is { } g ? new PhotoGeo(g.Lat, g.Lon, g.Alt) : null,
                await TakenAtAsync(p.TakenAt, p.Offset, ct)),
            PhotoRefused r => new PhotoRefusal(r.Reason switch
            {
                PhotoRefusalReason.TooLarge => PhotoRefusalCode.TooLarge,
                PhotoRefusalReason.TooManyPixels => PhotoRefusalCode.TooManyPixels,
                _ => PhotoRefusalCode.NotAnImage,
            }, r.Text),
            _ => throw new InvalidOperationException($"Неизвестный итог подготовки фото: {prepared.GetType().Name}."),
        };
    }

    private async Task<DateTimeOffset?> TakenAtAsync(DateTime? local, TimeSpan? offset, CancellationToken ct)
    {
        if (local is not { } at) return null;
        if (offset is { } known) return Moment(at, known);
        var zone = await settings.GetCompanyTimeZoneAsync(ct);
        // Час, которого в поясе нет (перевод часов вперёд), смещение даёт по ближайшему — снимок от этого
        // не становится чужим, а отказ из-за метаданных был бы хуже.
        return Moment(at, zone.GetUtcOffset(at));
    }

    /// <summary>
    /// Момент съёмки или <c>null</c>, если его не выразить: пояс дальше ±14 ч или время у края календаря
    /// («0001:01:01» с поясом +03:00 уходит за начало времён). Конструктор DateTimeOffset на таком
    /// бросает — и загрузка фото падала бы 500-м из-за метаданных, которые по замыслу просто теряются.
    /// </summary>
    public static DateTimeOffset? Moment(DateTime at, TimeSpan offset)
    {
        if (offset.Duration() > TimeSpan.FromHours(14) || offset.Ticks % TimeSpan.TicksPerMinute != 0) return null;
        var utc = at.Ticks - offset.Ticks;
        return utc < DateTime.MinValue.Ticks || utc > DateTime.MaxValue.Ticks ? null : new DateTimeOffset(at, offset);
    }
}
