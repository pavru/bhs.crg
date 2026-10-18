using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Профили печати в резервной копии (issue #1454): едут в любой копии, а системный профиль копия
/// другой установки не плодит — у него идентификатор-константа, и он ложится параметрами на здешний.
/// </summary>
public partial class BackupServiceTests
{
    /// <summary>
    /// Копия с правленым системным и своим профилем — на установку, где системный заводской, а своих нет
    /// (как у только что поставленной). Дважды: повторное восстановление ничего не удваивает.
    /// </summary>
    [Fact]
    public async Task Профили_печати_едут_в_копии_и_второго_системного_не_появляется()
    {
        try
        {
            await PrintProfilesState.ResetAsync(fixture.Services);
            Guid ownId;
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.RenditionLayoutProfiles.SingleAsync(p => p.IsSystem)).Update(
                    RenditionLayoutProfile.SystemName, PaperFormat.A3, PageOrientation.Landscape, PageFit.None, new(8, 8, 8, 8));
                var own = RenditionLayoutProfile.Create("Узкий", PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, new(3, 3, 3, 3));
                ownId = own.Id;
                db.RenditionLayoutProfiles.Add(own);
                await db.SaveChangesAsync();
            }

            var zip = await ExportedAsync();
            await fixture.ResetDatabaseAsync();
            await PrintProfilesState.ResetAsync(fixture.Services);

            for (var i = 0; i < 2; i++)
                using (var scope = fixture.Services.CreateScope())
                    Assert.True((await Backup(scope).ImportAsync(new MemoryStream(zip))).Success);

            using (var scope = fixture.Services.CreateScope())
            {
                var profiles = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .RenditionLayoutProfiles.AsNoTracking().OrderByDescending(p => p.IsSystem).ToListAsync();
                Assert.Equal(2, profiles.Count);
                var system = profiles[0];
                Assert.Equal((RenditionLayoutProfile.SystemId, true, PaperFormat.A3, PageFit.None, 8),
                    (system.Id, system.IsSystem, system.Paper, system.Fit, system.MarginTop));
                Assert.Equal((ownId, "Узкий", false, 3), (profiles[1].Id, profiles[1].Name, profiles[1].IsSystem, profiles[1].MarginLeft));
            }
        }
        finally
        {
            await PrintProfilesState.ResetAsync(fixture.Services);
        }
    }

    /// <summary>Свой профиль, чьё имя здесь занято другим, пропускается — и пропуск назван.</summary>
    [Fact]
    public async Task Профиль_печати_с_занятым_именем_пропускается_с_предупреждением()
    {
        try
        {
            await PrintProfilesState.ResetAsync(fixture.Services);
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.RenditionLayoutProfiles.Add(RenditionLayoutProfile.Create(
                    "Узкий", PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, new(3, 3, 3, 3)));
                await db.SaveChangesAsync();
            }
            var zip = await ExportedAsync();

            await PrintProfilesState.ResetAsync(fixture.Services);
            Guid hereId;
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var here = RenditionLayoutProfile.Create("узкий", PaperFormat.A5, PageOrientation.Portrait, PageFit.Width, new(3, 3, 3, 3));
                hereId = here.Id;
                db.RenditionLayoutProfiles.Add(here);
                await db.SaveChangesAsync();
            }

            using (var scope = fixture.Services.CreateScope())
            {
                var report = await Backup(scope).ImportAsync(new MemoryStream(zip));
                Assert.True(report.Success);
                Assert.Contains(report.Warnings, w => w.Contains("профилей печати") && w.Contains("уже есть"));
            }

            using (var scope = fixture.Services.CreateScope())
            {
                var own = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .RenditionLayoutProfiles.AsNoTracking().Where(p => !p.IsSystem).ToListAsync());
                Assert.Equal((hereId, PaperFormat.A5), (own.Id, own.Paper));
            }
        }
        finally
        {
            await PrintProfilesState.ResetAsync(fixture.Services);
        }
    }
}
