using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Storage;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio.Exceptions;

namespace BHS.CRG.Tests.Storage;

[Collection("Integration")]
public class ModuleBlobsPortTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Отсутствующий_объект_переводится_в_доменный_отказ()
    {
        var port = new ModuleBlobsPort(Storage(new ObjectNotFoundException("object missing")), "work");
        var path = await port.PutAsync("photo.jpg", new MemoryStream([1, 2, 3]), "image/jpeg");
        await Assert.ThrowsAsync<NotFoundException>(() => port.OpenAsync(path));
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("access")]
    [InlineData("transport")]
    [InlineData("cancelled")]
    public async Task Другой_отказ_не_считается_отсутствием(string kind)
    {
        Exception failure = kind switch
        {
            "bucket" => new BucketNotFoundException("bucket missing"),
            "access" => new AccessDeniedException("access denied"),
            "transport" => new IOException("connection lost"),
            _ => new OperationCanceledException(),
        };
        var port = new ModuleBlobsPort(Storage(failure), "work");
        var path = await port.PutAsync("photo.jpg", new MemoryStream([1, 2, 3]), "image/jpeg");

        Assert.Same(failure, await Record.ExceptionAsync(() => port.OpenAsync(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ядро_и_копия_сохраняют_исходный_отказ_потери_файла(bool backup)
    {
        var missing = new ObjectNotFoundException("object missing");
        var storage = Storage(missing);
        var owner = backup ? "work" : BlobOwner.Core;
        var path = await storage.UploadAsync("photo.jpg", new MemoryStream([1, 2, 3]), "image/jpeg", owner);

        Assert.Same(missing, await Record.ExceptionAsync(() => storage.DownloadAsync(path, backup ? BlobOwner.Any : owner)));
    }

    private RegisteredBlobStorage Storage(Exception failure) =>
        new(new Refusing(failure), fixture.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<RegisteredBlobStorage>.Instance);

    private sealed class Refusing(Exception failure) : FakeBlobStorage
    {
        public override Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default) =>
            Task.FromException<Stream>(failure);
    }
}
