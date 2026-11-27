using System.Text;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Work;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private const string Notifications = "/api/notifications";
    private const string Activity = "/api/activity";
    private const string Search = "/api/search";
    private const string Attachments = "/api/attachments";
    private const string CoreLabel = "Справочная запись теста";
    private const string PersonalMessage = "Личное уведомление посева";
    private const string DigestTitle = "Подано отчётов: 1";

    private static readonly string[] CoreProbeNames =
    [
        $"GET {Notifications}", $"GET {Activity}", $"GET {Search} (стройка)", $"GET {Search} (отчёт)",
        $"GET {Attachments} (файл ядра)", $"GET {Attachments} (два файла Work)",
    ];

    private sealed record CoreSeed(HttpClient Client, Guid Activity, Guid Notification, string BlobPath, byte[] File,
        string[] WorkPaths) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private async Task<CoreSeed> PrepareCoreAsync()
    {
        var (account, client) = await world.Assignments.UserAsync("Читатель ядра", CorePermissions.AuditRead,
            CorePermissions.FilesUse, CorePermissions.ConstructionsRead, CorePermissions.DataSetsRead);
        try
        {
            using var scope = testHost.Services.CreateScope();
            var services = scope.ServiceProvider;
            var notification = await services.GetRequiredService<INotificationService>().PublishAsync(
                NotificationSeverity.Info, CoreLabel, PersonalMessage, userId: installerAccount);
            var db = services.GetRequiredService<AppDbContext>();
            var activity = ActivityRecord.Create(ActivityActions.TableExported.Code, account, "Читатель ядра", targetLabel: CoreLabel);
            db.ActivityRecords.Add(activity);
            await db.SaveChangesAsync();
            var bytes = Encoding.UTF8.GetBytes(CoreLabel);
            await using var file = new MemoryStream(bytes);
            var path = await services.GetRequiredService<IBlobStorage>().UploadAsync("work25-core.txt", file, "text/plain");
            var work = Assert.Single(await new PhotoWorld(testHost).PhotosAsync(), photo => photo.Id == photoB);
            await new ReviewWorld(testHost).TickAsync(ReviewWorld.Up19.ToUniversalTime());
            return new(client, activity.Id, notification, path, bytes,
                [Assert.IsType<string>(work.BlobPath), Assert.IsType<string>(work.PreviewBlobPath)]);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void RegisterCoreProbes(ProbeCatalog catalog, IsolationReaders readers, CoreSeed seed)
    {
        catalog.Add($"GET {Notifications}", "личное уведомление и точный счётчик сводки Work", async () =>
        {
            await CheckResponseAsync(readers.Installer, Notifications, Ok, inspect: body =>
            {
                var item = Assert.Single(Items(body), row => row.GetProperty("id").GetGuid() == seed.Notification);
                Assert.Equal(PersonalMessage, item.GetProperty("message").GetString());
                Assert.Equal(1, body.GetProperty("unreadCount").GetInt32());
            });
            foreach (var client in new[] { readers.Unassigned, readers.Executive })
                await CheckResponseAsync(client, Notifications, Ok, inspect: body =>
                    Assert.DoesNotContain(Items(body), row => row.GetProperty("id").GetGuid() == seed.Notification));
            await CheckResponseAsync(readers.Manager, Notifications, Ok, inspect: body =>
            {
                var digest = Assert.Single(Items(body), item => item.GetProperty("title").GetString() == DigestTitle);
                Assert.Equal("Ждут приёмки: Стройка Б — 1", digest.GetProperty("message").GetString());
            });
        });

        catalog.Add($"GET {Activity}", "справочная запись и разрешённые счётчики выгрузок", async () =>
        {
            foreach (var client in new[] { readers.Installer, readers.Unassigned, readers.Executive })
                await CheckResponseAsync(client, Activity, Closed);
            await CheckResponseAsync(seed.Client, $"{Activity}?action={ActivityActions.TableExported.Code}", Ok, inspect: body =>
            {
                var items = Items(body).ToArray();
                var reference = Assert.Single(items, row => row.GetProperty("id").GetGuid() == seed.Activity);
                Assert.Equal(CoreLabel, reference.GetProperty("target").GetString());
                var exported = items.Where(row => row.GetProperty("id").GetGuid() != seed.Activity).ToArray();
                Assert.NotEmpty(exported);
                Assert.All(exported, row =>
                {
                    Assert.Contains(row.GetProperty("target").GetString(), new[] { "Отчёты монтажников", "Строки работы" });
                    Assert.Contains(row.GetProperty("after").GetString(), new[] { "строк: 0", "строк: 1", "строк: 2" });
                    Assert.Null(row.GetProperty("before").GetString());
                });
            });
            await CheckResponseAsync(seed.Client, Activity, Ok);
        });

        catalog.Add($"GET {Search} (стройка)", "доступные группы и справочный ID стройки", () => CheckResponseAsync(seed.Client,
            $"{Search}?q={Uri.EscapeDataString("Стройка Б")}", Ok, inspect: body =>
            {
                var groups = body.GetProperty("groups").EnumerateArray().ToArray();
                Assert.Equal(new[] { "constructions", "datasets" }, groups.Select(group => group.GetProperty("code").GetString()));
                var site = Assert.Single(Items(groups[0]), item => item.GetProperty("id").GetGuid() == world.SiteB);
                Assert.Equal("Стройка Б", site.GetProperty("title").GetString());
            }));

        catalog.Add($"GET {Search} (отчёт)", "доступный каталог без содержимого отчёта", async () =>
        {
            foreach (var client in new[] { seed.Client, readers.Installer, readers.Executive })
                await CheckResponseAsync(client, $"{Search}?q={marker}", Ok, inspect: body =>
                {
                    var groups = body.GetProperty("groups").EnumerateArray().ToArray();
                    Assert.NotEmpty(groups);
                    Assert.All(groups, group =>
                    {
                        Assert.Equal("core", group.GetProperty("module").GetString());
                        Assert.Empty(Items(group));
                    });
                });
        });

        catalog.Add($"GET {Attachments} (файл ядра)", "доступ по праву и точные байты", async () =>
        {
            var path = Attachment(seed.BlobPath);
            await CheckResponseAsync(readers.Installer, path, Closed);
            using var response = await seed.Client.GetAsync(path);
            Assert.Equal(Ok, response.StatusCode);
            Assert.Equal(seed.File, await response.Content.ReadAsByteArrayAsync());
        });

        catalog.Add($"GET {Attachments} (два файла Work)", "отказ по общему файловому адресу", async () =>
        {
            foreach (var path in seed.WorkPaths)
                await CheckResponseAsync(seed.Client, Attachment(path), Missing);
        });

        static string Attachment(string path) => $"{Attachments}?path={Uri.EscapeDataString(path)}";
    }
}
