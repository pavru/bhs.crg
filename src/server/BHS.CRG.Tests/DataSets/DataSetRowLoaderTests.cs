using System.Text;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Ветвление извлечения в DataSetRowLoader: обычные форматы — перепарсинг блоба на каждый вызов,
/// PDF — только закэшированные распознанные строки (CachedData), системный набор — провайдер
/// консолидации. Блоба во втором и третьем случае нет вовсе.
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
        public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
            => Task.FromResult(content?.LongLength);
    }

    /// <summary>Провайдер, отдающий одну заранее заданную строку и запоминающий контекст вызова.</summary>
    private sealed class FakeProvider : ISystemDataProvider
    {
        public CatalogScope? SeenScope;
        public Guid? SeenScopeId;
        public string? SeenMarker;

        /// <summary>Объявление как у настоящих «Документов комплекта» — иначе ворота не пустят.</summary>
        public SystemDataSetDeclaration Declaration { get; set; } = new(
            "id", "id.document.read", SystemDataSetIsolation.None, ["Отдаёт документы комплекта"]);

        public bool Handles(string marker) => marker == SystemDataSets.SetDocumentsMarker;

        public Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
            CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct) => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

        public DataAccess? SeenAccess;

        public Task<DataSetParseResult> ProvideAsync(
            string marker, CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
        {
            SeenAccess = access;
            SeenMarker = marker;
            SeenScope = scope;
            SeenScopeId = scopeId;
            return Task.FromResult(new DataSetParseResult(
                [new DataSetColumnInfo("Наименование", ["АОСР 1"])],
                [new Dictionary<string, string?> { ["Наименование"] = "АОСР 1" }]));
        }
    }

    private static DataSetRowLoader Loader(FakeBlob blob, ISystemDataProvider? provider = null)
        => new(blob, new DataSetParserFactory([new CsvDataSetParser()]),
            new SystemDataProviderRegistry(provider is null ? [] : [provider]));

    private static DataSetSource Source(
        DataSetFormat format, string blobPath, string? cachedData = null,
        string sheetOrPath = "default", Guid? scopeId = null)
    {
        var file = format == DataSetFormat.System
            ? DataSetFile.CreateSystem("Данные системы", CatalogScope.Set, scopeId ?? Guid.NewGuid())
            : DataSetFile.Create("Файл", format, blobPath, CatalogScope.Set, scopeId ?? Guid.NewGuid());
        var source = file.AddSource("Источник", sheetOrPath, "[]", 0, cachedData: cachedData);
        // Навигацию File в домене заполняет EF; в юнит-тесте — напрямую.
        typeof(DataSetSource).GetProperty(nameof(DataSetSource.File))!.SetValue(source, file);
        return source;
    }

    [Fact]
    public async Task CsvSource_ParsesBlobOnEveryCall()
    {
        var blob = new FakeBlob(Encoding.UTF8.GetBytes("Имя,Количество\nКабель,10\n"));
        var source = Source(DataSetFormat.Csv, "bucket/file.csv");

        var rows = await Loader(blob).LoadRowsAsync(source, TestAccess.All, default);

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

        var rows = await Loader(blob).LoadRowsAsync(source, TestAccess.All, default);

        Assert.Single(rows);
        Assert.Equal("Значение", rows[0]["Колонка"]);
        Assert.Equal(0, blob.Downloads);
    }

    [Fact]
    public async Task PdfSource_EmptyOrBrokenCache_YieldsNoRows()
    {
        var blob = new FakeBlob();
        Assert.Empty(await Loader(blob).LoadRowsAsync(Source(DataSetFormat.Pdf, "b/p.pdf"), TestAccess.All, default));
        Assert.Empty(await Loader(blob).LoadRowsAsync(
            Source(DataSetFormat.Pdf, "b/p.pdf", cachedData: "не json"), TestAccess.All, default));
    }

    [Fact]
    public async Task SystemSource_AsksProvider_WithScopeOfFile()
    {
        var blob = new FakeBlob(); // упадёт при любом скачивании
        var provider = new FakeProvider();
        var setId = Guid.NewGuid();
        var source = Source(DataSetFormat.System, "", sheetOrPath: SystemDataSets.SetDocumentsMarker, scopeId: setId);

        var rows = await Loader(blob, provider).LoadRowsAsync(source, TestAccess.All, default);

        Assert.Single(rows);
        Assert.Equal("АОСР 1", rows[0]["Наименование"]);
        Assert.Equal(SystemDataSets.SetDocumentsMarker, provider.SeenMarker);
        Assert.Equal(CatalogScope.Set, provider.SeenScope);
        Assert.Equal(setId, provider.SeenScopeId);
        Assert.Equal(0, blob.Downloads);
    }

    /// <summary>Обработка источника общая для всех видов извлечения — системный не исключение.</summary>
    [Fact]
    public async Task SystemSource_GoesThroughRowFilter()
    {
        var source = Source(DataSetFormat.System, "", sheetOrPath: SystemDataSets.SetDocumentsMarker);
        source.SetProcessing(
            """{"type":"condition","column":"Наименование","op":"eq","value":"нет такого"}""",
            null, null);

        Assert.Empty(await Loader(new FakeBlob(), new FakeProvider()).LoadRowsAsync(source, TestAccess.All, default));
    }

    /// <summary>
    /// Отказ битого отбора приходит из ОБЩЕГО пайплайна и называет источник по имени (issue #966).
    /// Проверяется здесь, а не только на исполнителе: имя источника знает загрузчик, и если он его
    /// не передаст, отказ дойдёт до человека безымянным — а исполнитель об этом не узнает.
    /// Пайплайн один на все пять путей чтения (предпросмотр, выгрузка, генерация, MCP, сверка),
    /// поэтому отказ появляется во всех сразу.
    /// </summary>
    [Fact]
    public async Task Битый_отбор_отказывает_и_называет_источник()
    {
        var blob = new FakeBlob(Encoding.UTF8.GetBytes("Имя,Количество\nКабель,10\n"));
        var source = Source(DataSetFormat.Csv, "bucket/file.csv");
        // Оператор, которого нет: годный JSON с негодным условием — так битый отбор и выглядит в
        // живой базе (колонка jsonb ломаного текста не принимает вовсе).
        source.SetProcessing("""{"type":"condition","column":"Имя","op":"betwen","value":"5"}""", null, null);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Loader(blob).LoadRowsAsync(source, TestAccess.All, default));

        Assert.Contains("Источник", refusal.Message);   // имя источника из Source(...)
    }

    [Fact]
    public async Task SystemSource_UnknownMarker_Throws()
    {
        var source = Source(DataSetFormat.System, "", sheetOrPath: "system:нет-такого");
        await Assert.ThrowsAsync<ConflictException>(
            () => Loader(new FakeBlob(), new FakeProvider()).LoadRowsAsync(source, TestAccess.All, default));
    }
}
