using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

// Порт подготовки фото (work-05, ТЗ WORK-33, CORE-34) на живом приложении.
public partial class ModulePortsTests
{
    private static readonly PhotoLimits PhotoLimits = new(
        MaxBytes: 1024 * 1024, MaxSide: 2400, PreviewSide: 480, MaxPixels: PhotoLimits.DefaultMaxPixels);

    /// <summary>
    /// Модуль получает порт фото из приложения и получает подготовленный JPEG с геометкой и временем —
    /// пояс времени при этом из снимка.
    /// </summary>
    [Fact]
    public async Task Фото_модуля_готовится_портом_ядра()
    {
        using var scope = host.Services.CreateScope();
        var photos = scope.ServiceProvider.GetRequiredService<IModulePhotos>();
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(3200, 2400),
            PhotoSamples.Exif(orientation: 6, geo: PhotoSamples.Place, takenAt: "2026:11:13 10:20:30", offset: "+05:00"));

        var photo = Assert.IsType<PreparedPhoto>(await photos.PrepareAsync(new MemoryStream(shot), PhotoLimits));

        Assert.Equal((1800, 2400), (photo.Width, photo.Height));
        Assert.NotNull(photo.Geo);
        Assert.Equal(PhotoSamples.Place.Lat, photo.Geo.Lat, 4);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 20, 30, TimeSpan.FromHours(5)), photo.TakenAt);
        Assert.NotEmpty(photo.Preview);
    }

    /// <summary>
    /// Время без пояса в снимке — в поясе компании, а не сервера: тот же пояс, которым система считает
    /// «сегодня».
    /// </summary>
    [Fact]
    public async Task Время_съёмки_без_пояса_берёт_пояс_компании()
    {
        using var scope = host.Services.CreateScope();
        var zone = await scope.ServiceProvider
            .GetRequiredService<BHS.CRG.Application.Settings.IAppSettingsStore>().GetCompanyTimeZoneAsync();
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(400, 300), PhotoSamples.Exif(takenAt: "2026:07:01 12:00:00"));

        var photo = Assert.IsType<PreparedPhoto>(
            await scope.ServiceProvider.GetRequiredService<IModulePhotos>().PrepareAsync(new MemoryStream(shot), PhotoLimits));

        var local = new DateTime(2026, 7, 1, 12, 0, 0);
        Assert.Equal(new DateTimeOffset(local, zone.GetUtcOffset(local)), photo.TakenAt);
    }

    /// <summary>
    /// Отказ лимита — значение, и после него в реестре блобов ничего не появилось: порт в хранилище не
    /// пишет вовсе.
    /// </summary>
    [Fact]
    public async Task Отказ_лимита_фото_не_оставляет_записи_в_реестре_блобов()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.BlobRegistry.CountAsync();

        var refusal = Assert.IsType<PhotoRefusal>(await scope.ServiceProvider.GetRequiredService<IModulePhotos>()
            .PrepareAsync(new MemoryStream(new byte[2 * 1024 * 1024]), PhotoLimits));
        var prepared = await scope.ServiceProvider.GetRequiredService<IModulePhotos>()
            .PrepareAsync(new MemoryStream(PhotoSamples.Jpeg(400, 300)), PhotoLimits);

        Assert.Equal(PhotoRefusalCode.TooLarge, refusal.Code);
        Assert.Equal("Файл больше 1 МБ.", refusal.Text);
        Assert.IsType<PreparedPhoto>(prepared);
        Assert.Equal(before, await db.BlobRegistry.CountAsync());
    }
}
