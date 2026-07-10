using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public enum DataSetFormat { Csv, Xlsx, Xls, Xml, Json, Zip, Pdf }

public class DataSetFile : Entity
{
    public string Name { get; private set; } = null!;
    public DataSetFormat Format { get; private set; }
    public string BlobPath { get; private set; } = null!;
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    private readonly List<DataSetSource> _sources = [];
    public IReadOnlyList<DataSetSource> Sources => _sources.AsReadOnly();

    /// <summary>
    /// Препроцессинг (issue #27/#28): хардкод-профиль распознавания, породивший структуру набора
    /// (для PDF — «Gost»/«Invoice»). Null — препроцессинга нет (CSV/XLSX/XML/JSON — уже структурны).
    /// </summary>
    public string? PreprocessingProfile { get; private set; }

    /// <summary>
    /// Авторитетная группировка страниц набора (JSONB, <see cref="GostGroupingData"/> с id групп) —
    /// источник истины препроцессинга. Проекции (обложка/титул/документы/таблицы) — производные
    /// источники, пересчитываемые отсюда в одной точке. Ранее жила на источнике gost-documents.
    /// </summary>
    public string? Grouping { get; private set; }

    /// <summary>
    /// true — блоб заменён ПОСЛЕ распознавания: группировка/проекции относятся к прежнему содержимому.
    /// Сбрасывается при следующем распознавании. Ранее <see cref="DataSetSource.RecognitionStale"/>.
    /// </summary>
    public bool RecognitionStale { get; private set; }

    /// <summary>Сырьё профиля «Счёт на оплату» (issue #44) — JSON {Header, LineItems}, аналог
    /// <see cref="Grouping"/> для ГОСТ (иная, непостраничная форма — своя колонка, не обобщение).
    /// Пишется распознаванием; источники «Шапка»/«Товары» — кандидаты, проецируются пользователем.</summary>
    public string? InvoiceRawData { get; private set; }

    private DataSetFile() { }

    public static DataSetFile Create(string name, DataSetFormat format, string blobPath,
        CatalogScope scope, Guid? scopeId)
        => new() { Name = name, Format = format, BlobPath = blobPath, Scope = scope, ScopeId = scopeId };

    public DataSetSource AddSource(string name, string sheetOrPath, string cachedSchema, int cachedRowCount,
        string? columnExpressions = null, string? cachedData = null)
    {
        var src = DataSetSource.Create(Id, name, sheetOrPath, cachedSchema, cachedRowCount, columnExpressions, cachedData);
        _sources.Add(src);
        TouchUpdatedAt();
        return src;
    }

    public void ReplaceAllSources(IEnumerable<DataSetSource> sources)
    {
        _sources.Clear();
        _sources.AddRange(sources);
        TouchUpdatedAt();
    }

    public void UpdateName(string name) { Name = name.Trim(); TouchUpdatedAt(); }

    public void UpdateBlobPath(string newBlobPath, DataSetFormat newFormat)
    {
        BlobPath = newBlobPath;
        Format = newFormat;
        TouchUpdatedAt();
    }

    /// <summary>Задать профиль препроцессинга набора (issue #28). Null — снять.</summary>
    public void SetPreprocessingProfile(string? profile)
    {
        PreprocessingProfile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        TouchUpdatedAt();
    }

    /// <summary>Задать/обновить авторитетную группировку набора (JSON GostGroupingData) и снять stale.</summary>
    public void SetGrouping(string? groupingJson)
    {
        Grouping = groupingJson;
        RecognitionStale = false;
        TouchUpdatedAt();
    }

    /// <summary>Пишет сырьё профиля «Счёт на оплату» (issue #44) — аналог SetGrouping для ГОСТ.</summary>
    public void SetInvoiceRawData(string? rawDataJson)
    {
        InvoiceRawData = rawDataJson;
        RecognitionStale = false;
        TouchUpdatedAt();
    }

    /// <summary>Пометить набор устаревшим — блоб заменён после распознавания (данные к прежнему файлу).</summary>
    public void MarkRecognitionStale()
    {
        RecognitionStale = true;
        TouchUpdatedAt();
    }
}
