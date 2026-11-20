using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Domain.Common;
using BHS.CRG.Tests.Integration;
using Minio.Exceptions;

namespace BHS.CRG.Tests.Storage;

public class ModuleBlobsPortTests
{
    [Fact]
    public async Task Отсутствующий_объект_переводится_в_доменный_отказ()
    {
        var port = new ModuleBlobsPort(new Refusing(new ObjectNotFoundException("object missing")), "work");
        await Assert.ThrowsAsync<NotFoundException>(() => port.OpenAsync("bucket/photo.jpg"));
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
        var port = new ModuleBlobsPort(new Refusing(failure), "work");

        Assert.Same(failure, await Record.ExceptionAsync(() => port.OpenAsync("bucket/photo.jpg")));
    }

    private sealed class Refusing(Exception failure) : FakeBlobStorage
    {
        public override Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default) =>
            Task.FromException<Stream>(failure);
    }
}
