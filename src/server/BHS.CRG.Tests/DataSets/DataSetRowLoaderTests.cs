using System.Text;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Ветвление извлечения в DataSetRowLoader: обычные форматы — перепарсинг блоба на каждый вызов,
/// PDF — только закэшированные распознанные строки (CachedData), блоб не трогается.
/// </summary>
public class DataSetRowLoaderTests
{
    /// <summary>Блоб-хранилище одного файла; считает скачивания и падает, если файла нет.</summary>
    private sealed class FakeBlob(byte[]? content = null) : IBlobStorage
    {
        public int Downloads;
        public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
        {
            Downloads++;
            if (content is null) throw new InvalidOperationException("Блоб недоступен — скачивания не ожидалось.");
            return Task.FromResult<Stream>(new MemoryStream(content));
        }
        public Task<string> UploadAsync(string fileName, Stream s, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string blobPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PutAsync(string blobPath, Stream s, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static DataSetRowLoader Loader(FakeBlob blob)
        => new(blob, new DataSetParserFactory([new CsvDataSetParser()]));

    private static DataSetSource Source(DataSetFormat format, string blobPath, string? cachedData = null)
    {
        var file = DataSetFile.Create("Файл", format, blobPath, CatalogScope.Set, Guid.NewGuid());
        var source = file.AddSource("Источник", "default", "[]", 0, cachedData: cachedData);
        // Навигацию File в домене заполняет EF; в юнит-тесте — напрямую.
        typeof(DataSetSource).GetProperty(nameof(DataSetSource.File))!.SetValue(source, file);
        return source;
    }

    [Fact]
    public async Task CsvSource_ParsesBlobOnEveryCall()
    {
        var blob = new FakeBlob(Encoding.UTF8.GetBytes("Имя,Количество\nКабель,10\n"));
        var source = Source(DataSetFormat.Csv, "bucket/file.csv");

        var rows = await Loader(blob).LoadRowsAsync(source, default);

        Assert.Single(rows);
        Assert.Equal("Кабель", rows[0]["Имя"]);
        Assert.Equal(1, blob.Downloads);
    }

    [Fact]
    public async Task PdfSource_ReadsCachedData_WithoutTouchingBlob()
    {
        var blob = new FakeBlob(); // упадёт при любом скачивании
        var source = Source(DataSetFormat.Pdf, "bucket/file.pdf",
            cachedData: """[{"Колонка":"Значение"}]""");

        var rows = await Loader(blob).LoadRowsAsync(source, default);

        Assert.Single(rows);
        Assert.Equal("Значение", rows[0]["Колонка"]);
        Assert.Equal(0, blob.Downloads);
    }

    [Fact]
    public async Task PdfSource_EmptyOrBrokenCache_YieldsNoRows()
    {
        var blob = new FakeBlob();
        Assert.Empty(await Loader(blob).LoadRowsAsync(Source(DataSetFormat.Pdf, "b/p.pdf"), default));
        Assert.Empty(await Loader(blob).LoadRowsAsync(
            Source(DataSetFormat.Pdf, "b/p.pdf", cachedData: "не json"), default));
    }
}
