using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class DataSetRecognitionRevisionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupRestoresDataSetWithANewRevision(bool restoreOverExisting)
    {
        var fileId = await SeedAsync(fixture.Services);
        var before = await ReadAsync(fixture.Services, fileId);
        using var copy = new MemoryStream();
        using (var scope = fixture.Services.CreateScope())
        {
            var (stream, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
            await using var zip = stream;
            await zip.CopyToAsync(copy);
        }

        if (restoreOverExisting)
        {
            using var scope = fixture.Services.CreateScope();
            var file = (await Db(scope).DataSetFiles.FindAsync(fileId))!;
            file.UpdateName("Имя после снятия копии");
            await Db(scope).SaveChangesAsync();
        }
        else
        {
            await fixture.ResetDatabaseAsync();
        }

        copy.Position = 0;
        using (var scope = fixture.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(copy);
            Assert.True(report.Success, string.Join("; ", report.Warnings));
        }

        var restored = await ReadAsync(fixture.Services, fileId);
        Assert.NotEqual(Guid.Empty, restored.Revision);
        Assert.NotEqual(before.Revision, restored.Revision);
        Assert.Equal(before with { Revision = restored.Revision }, restored);
        await AssertReadableAsync(fixture.Services, restored);
    }

    [Theory]
    [InlineData("grouping")]
    [InlineData("original")]
    [InlineData("profile")]
    public async Task QueuedRecognitionChecksInputsBeforeCallingTheEngine(string changed)
    {
        var engine = new ControlledRecognizer();
        using var host = HostWith(engine, new RecordingBlobs());
        var fileId = await SeedAsync(host.Services);
        var jobId = await LaunchAsync(host.Services, fileId, confirm: true);
        var original = await ReadAsync(host.Services, fileId);

        using (var edit = host.Services.CreateScope())
        {
            var dataSets = edit.ServiceProvider.GetRequiredService<IDataSetService>();
            switch (changed)
            {
                case "grouping":
                    await dataSets.ApplyGroupingAsync(fileId, Grouping("Новая схема"), default);
                    break;
                case "original":
                    await dataSets.ReplaceFileAsync(fileId, new(MakePdf(), "replaced.pdf", "application/pdf", null), default);
                    break;
                case "profile":
                    var profile = await Db(edit).RecognitionProfiles.FirstAsync(p => p.Kind == RecognitionProfileKind.TitleBlock);
                    Assert.True(await edit.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>()
                        .SetFileRecognitionProfilesAsync(fileId,
                            new Dictionary<string, Guid?> { [nameof(RecognitionProfileKind.TitleBlock)] = profile.Id }, default));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changed));
            }
        }
        var current = await ReadAsync(host.Services, fileId);
        Assert.NotEqual(original.Revision, current.Revision);
        if (changed == "original")
        {
            var blob = host.Services.GetRequiredService<IBlobStorage>();
            Assert.Null(await blob.GetSizeAsync(original.Original));
            Assert.NotNull(await blob.GetSizeAsync(current.Original));
        }

        using var worker = await StartWorkerAsync(host.Services);
        await AssertFailedAsync(host.Services, jobId, "Набор данных изменился");
        Assert.Equal(0, engine.Calls);
        Assert.Equal(current, await ReadAsync(host.Services, fileId));
        await AssertReadableAsync(host.Services, current);
    }

    [Fact]
    public async Task ConcurrentGroupingKeepsTheWinningProjectionAndFiles()
    {
        var engine = new ControlledRecognizer(pause: true);
        var blobs = new RecordingBlobs();
        using var host = HostWith(engine, blobs);
        var fileId = await SeedAsync(host.Services, manuallyEdited: false);
        var original = await ReadAsync(host.Services, fileId);
        var jobId = await LaunchAsync(host.Services, fileId, confirm: false);

        using var worker = await StartWorkerAsync(host.Services);
        Snapshot winner;
        string[] uploadedBeforeResult;
        try
        {
            await engine.Entered.WaitAsync(Deadline);
            using var editor = host.Services.CreateScope();
            await editor.ServiceProvider.GetRequiredService<IDataSetService>()
                .ApplyGroupingAsync(fileId, Grouping("Ручная схема"), default);
            winner = await ReadAsync(host.Services, fileId);
            uploadedBeforeResult = blobs.Uploaded.ToArray();
        }
        finally
        {
            engine.Resume();
        }

        await AssertFailedAsync(host.Services, jobId, "Набор данных изменился");
        Assert.NotEqual(original.Part, winner.Part);
        Assert.Equal(winner, await ReadAsync(host.Services, fileId));
        await AssertReadableAsync(host.Services, winner);
        Assert.Null(await blobs.GetSizeAsync(original.Part));
        var unusedPart = Assert.Single(blobs.Uploaded.Except(uploadedBeforeResult));
        Assert.Null(await blobs.GetSizeAsync(unusedPart));
    }

    [Fact]
    public async Task ConfirmedGroupingUsesItsRevisionAndCanBeAppliedOnlyOnce()
    {
        var engine = new ControlledRecognizer();
        using var host = HostWith(engine, new RecordingBlobs());
        var fileId = await SeedAsync(host.Services);
        var before = await ReadAsync(host.Services, fileId);
        var jobId = await LaunchAsync(host.Services, fileId, confirm: true);

        using (var edit = host.Services.CreateScope())
        {
            var file = (await Db(edit).DataSetFiles.FindAsync(fileId))!;
            file.UpdateName("Новое имя набора");
            await Db(edit).SaveChangesAsync();
        }
        Assert.Equal(before.Revision, (await ReadAsync(host.Services, fileId)).Revision);

        using var worker = await StartWorkerAsync(host.Services);
        var done = await ReadFinishedJobAsync(host.Services, jobId);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        var result = await ReadAsync(host.Services, fileId);
        Assert.Equal("Новое имя набора", result.Name);
        Assert.Contains("Распознанная схема", result.Projection);
        Assert.NotEqual(before.Revision, result.Revision);
        Assert.False(GostGroupingSerialization.Parse(result.Grouping)!.ManuallyEdited);
        await AssertReadableAsync(host.Services, result);

        var calls = engine.Calls;
        var retry = await EnqueueAsync(host.Services, fileId, done.Payload);
        await AssertFailedAsync(host.Services, retry, "Набор данных изменился");
        Assert.Equal(calls, engine.Calls);
        Assert.Equal(result, await ReadAsync(host.Services, fileId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    public async Task RecognitionWithMissingLaunchDataCanBeStartedAgain(string? payload)
    {
        var engine = new ControlledRecognizer();
        using var host = HostWith(engine, new RecordingBlobs());
        var fileId = await SeedAsync(host.Services);
        var original = await ReadAsync(host.Services, fileId);
        var jobId = await EnqueueAsync(host.Services, fileId, payload);

        using var worker = await StartWorkerAsync(host.Services);
        await AssertFailedAsync(host.Services, jobId, "Запустите распознавание заново");
        Assert.Equal(0, engine.Calls);
        Assert.Equal(original, await ReadAsync(host.Services, fileId));

        var newJob = await LaunchAsync(host.Services, fileId, confirm: true);
        Assert.Equal(JobStatus.Succeeded, (await ReadFinishedJobAsync(host.Services, newJob)).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupUsesPersistedReferencesWhenCommitCannotBeConfirmed(bool replaceOriginal, bool committed)
    {
        var blobs = new RecordingBlobs();
        using var host = HostWith(new ControlledRecognizer(), blobs);
        var fileId = await SeedAsync(host.Services);
        var original = await ReadAsync(host.Services, fileId);
        var previouslyUploaded = blobs.Uploaded.ToArray();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(IntegrationTestFixture.TestConnectionString)
            .AddInterceptors(new CommitFailure(committed)).Options;
        await using var db = new AppDbContext(options);
        using var scope = host.Services.CreateScope();
        if (replaceOriginal)
        {
            var service = ActivatorUtilities.CreateInstance<DataSetFileService>(scope.ServiceProvider, db);
            await Assert.ThrowsAsync<IOException>(() =>
                service.ReplaceFileAsync(fileId, new(MakePdf(), "new.pdf", "application/pdf", null), default));
        }
        else
        {
            var service = ActivatorUtilities.CreateInstance<DataSetPdfRecognitionService>(scope.ServiceProvider, db);
            await Assert.ThrowsAsync<IOException>(() => service.ApplyGroupingAsync(fileId, Grouping("Новая схема"), default));
        }

        var uploaded = Assert.Single(blobs.Uploaded.Except(previouslyUploaded));
        var current = await ReadAsync(host.Services, fileId);
        await AssertReadableAsync(host.Services, current);
        if (committed)
        {
            Assert.NotEqual(original.Revision, current.Revision);
            Assert.Equal(uploaded, replaceOriginal ? current.Original : current.Part);
            Assert.Null(await blobs.GetSizeAsync(replaceOriginal ? original.Original : original.Part));
        }
        else
        {
            Assert.Equal(original, current);
            Assert.Null(await blobs.GetSizeAsync(uploaded));
        }
    }

    private sealed class CommitFailure(bool committed) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default) =>
            committed ? ValueTask.FromResult(result) : throw new IOException("Ответ о завершении записи потерян.");

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) =>
            committed ? throw new IOException("Ответ о завершении записи потерян.") : Task.CompletedTask;
    }

    private WebApplicationFactory<Program> HostWith(ControlledRecognizer engine, RecordingBlobs blobs) =>
        fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveJobRunner();
            services.RemoveAll<IDocumentRecognizer>();
            services.AddSingleton<IDocumentRecognizer>(engine);
            services.RemoveAll<IRecognitionPreflight>();
            services.AddSingleton<IRecognitionPreflight, ReadyPreflight>();
            services.RemoveAll<FakeBlobStorage>();
            services.AddSingleton<FakeBlobStorage>(blobs);
        }));

    private sealed class ReadyPreflight : IRecognitionPreflight
    {
        public Task<RecognitionBlock?> CheckAsync(CancellationToken ct = default) => Task.FromResult<RecognitionBlock?>(null);
    }

    private sealed class ControlledRecognizer(bool pause = false) : IDocumentRecognizer
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public Task Entered => entered.Task;
        public void Resume() => resume.TrySetResult();

        public async Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (pause) await resume.Task.WaitAsync(ct);
            return new RecognitionResult(new Dictionary<string, string?>
            {
                ["ТипСтраницы"] = "Документ",
                ["Форма"] = "Форма3",
                ["Шифр"] = "А1",
                ["НаименованиеДокумента"] = "Распознанная схема",
            }, null);
        }
    }

    private sealed class RecordingBlobs : FakeBlobStorage
    {
        public ConcurrentQueue<string> Uploaded { get; } = new();

        public override async Task<string> UploadAsync(string name, Stream content, string contentType, CancellationToken ct = default)
        {
            var path = await base.UploadAsync(name, content, contentType, ct);
            Uploaded.Enqueue(path);
            return path;
        }
    }

    private static async Task<Guid> SeedAsync(IServiceProvider services, bool manuallyEdited = true)
    {
        using var scope = services.CreateScope();
        var db = Db(scope);
        var blob = services.GetRequiredService<IBlobStorage>();
        using var upload = new MemoryStream(MakePdf());
        var path = await blob.UploadAsync("album.pdf", upload, "application/pdf");
        var file = DataSetFile.Create("Альбом", DataSetFormat.Pdf, path, CatalogScope.System, null);
        file.SetPreprocessingProfile(PdfProfiles.GostTitleBlock);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0));
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IDataSetService>()
            .ApplyGroupingAsync(file.Id, Grouping("Исходная схема"), default);
        if (!manuallyEdited)
        {
            var grouping = GostGroupingSerialization.Parse(file.Grouping)!;
            file.SetGrouping(JsonSerializer.Serialize(grouping with { ManuallyEdited = false }));
            await db.SaveChangesAsync();
        }
        return file.Id;
    }

    private static ApplyGroupingInput Grouping(string name) =>
        new([new GostGroupingGroupDto(GostGroupKind.Document, "А1", name, [0])]);

    private static byte[] MakePdf()
    {
        using var pdf = new SharpPdfDocument();
        pdf.AddPage();
        using var bytes = new MemoryStream();
        pdf.Save(bytes, false);
        return bytes.ToArray();
    }

    private record Snapshot(Guid Revision, string Name, string Original, string Part, string Grouping, string Projection);

    private static async Task<Snapshot> ReadAsync(IServiceProvider services, Guid fileId)
    {
        using var scope = services.CreateScope();
        var db = Db(scope);
        var file = await db.DataSetFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        var documents = await db.DataSetSources.AsNoTracking().SingleAsync(s => s.FileId == fileId &&
            s.SheetOrPath == PdfProfiles.GostDocumentsMarker);
        var parts = GostGroupingSerialization.ReadPartPaths(file.Grouping)!;
        return new Snapshot(file.RecognitionRevision, file.Name, file.BlobPath, Assert.Single(parts),
            file.Grouping!, documents.CachedData!);
    }

    private static async Task AssertReadableAsync(IServiceProvider services, Snapshot snapshot)
    {
        Assert.Equal(snapshot.Part, Assert.Single(GostGroupingSerialization.ReadRowFilePaths(snapshot.Projection)!));
        var blob = services.GetRequiredService<IBlobStorage>();
        await using var original = await blob.DownloadAsync(snapshot.Original);
        await using var part = await blob.DownloadAsync(snapshot.Part);
        Assert.True(original.Length > 0);
        Assert.True(part.Length > 0);
    }

    private static AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private static async Task<Guid> LaunchAsync(IServiceProvider services, Guid fileId, bool confirm)
    {
        using var scope = services.CreateScope();
        var launch = await scope.ServiceProvider.GetRequiredService<IOperationLauncher>()
            .RecognizeFileAsync(fileId, Guid.Empty, confirm, default);
        return Assert.IsType<Guid>(launch?.JobId);
    }

    private static async Task<Guid> EnqueueAsync(IServiceProvider services, Guid fileId, string? payload)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobService>()
            .EnqueueAsync(JobKind.RecognizeGostSet, Guid.Empty, fileId, "Распознавание", payload, default);
    }

    private static async Task<JobBackgroundService> StartWorkerAsync(IServiceProvider services)
    {
        var worker = ActivatorUtilities.CreateInstance<JobBackgroundService>(services);
        await worker.StartAsync(default);
        return worker;
    }

    private static async Task<Job> ReadFinishedJobAsync(IServiceProvider services, Guid jobId)
    {
        var queue = services.GetRequiredService<JobQueue>();
        using var deadline = new CancellationTokenSource(Deadline);
        while (!queue.IsIdle) await Task.Delay(25, deadline.Token);
        using var scope = services.CreateScope();
        return await Db(scope).Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private static async Task AssertFailedAsync(IServiceProvider services, Guid jobId, string reason)
    {
        var job = await ReadFinishedJobAsync(services, jobId);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains(reason, job.Error);
        using var scope = services.CreateScope();
        var notifications = Db(scope).Notifications.AsNoTracking();
        Assert.True(await notifications.AnyAsync(n => n.Source == "Фоновые задачи" && n.Message == job.Error));
    }
}
