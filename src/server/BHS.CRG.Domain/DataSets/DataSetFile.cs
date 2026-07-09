using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public enum DataSetFormat { Csv, Xlsx, Xls, Xml, Json, Zip, Pdf }

/// <summary>
/// Происхождение набора (issue #38): загружен пользователем (исходный комплект — распознаётся) либо
/// вырезан распознаванием исходного комплекта (несёт готовые распознанные строки — источники их
/// проецируют, повторно не распознаётся).
/// </summary>
public enum DataSetFileOrigin { Uploaded, DerivedFromRecognition }

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

    /// <summary>Происхождение набора (issue #38). Uploaded — исходный комплект; DerivedFromRecognition — вырезанный.</summary>
    public DataSetFileOrigin Origin { get; private set; } = DataSetFileOrigin.Uploaded;

    /// <summary>Для derived-набора — исходный набор-комплект, из которого он вырезан. Null для Uploaded.</summary>
    public Guid? ParentFileId { get; private set; }

    /// <summary>Стабильный ключ идентичности derived-набора в рамках родителя (стабильный id группы из #28
    /// либо служебный маркер части: "documents"/"cover"/"titlepage") — для upsert при перераспознавании.</summary>
    public string? OriginKey { get; private set; }

    /// <summary>Несомые распознанные строки derived-набора (JSON-массив) — «сырьё», из которого источники
    /// проецируют данные (без повторного распознавания). Null для Uploaded.</summary>
    public string? RecognizedData { get; private set; }

    /// <summary>Схема несомых распознанных строк (JSON [{name,sampleValues}]) — для превью/кандидата источника.</summary>
    public string? RecognizedSchema { get; private set; }

    private DataSetFile() { }

    public static DataSetFile Create(string name, DataSetFormat format, string blobPath,
        CatalogScope scope, Guid? scopeId)
        => new() { Name = name, Format = format, BlobPath = blobPath, Scope = scope, ScopeId = scopeId };

    /// <summary>
    /// Вырезанный распознаванием набор (issue #38): наследует scope родителя, несёт готовые распознанные
    /// строки + схему, blobPath — вырезанный под-PDF (провенанс/скачивание). OriginKey — стабильный ключ
    /// идентичности в рамках родителя (для upsert при перераспознавании).
    /// </summary>
    public static DataSetFile CreateDerived(
        string name, string blobPath, DataSetFile parent, string originKey,
        string recognizedData, string recognizedSchema, int rowCount)
        => new()
        {
            Name = name,
            Format = DataSetFormat.Pdf,
            BlobPath = blobPath,
            Scope = parent.Scope,
            ScopeId = parent.ScopeId,
            Origin = DataSetFileOrigin.DerivedFromRecognition,
            ParentFileId = parent.Id,
            OriginKey = originKey,
            RecognizedData = recognizedData,
            RecognizedSchema = recognizedSchema,
        };

    /// <summary>Обновить несомые распознанные данные derived-набора (перераспознавание) + вырезанный блоб.</summary>
    public void UpdateRecognizedData(string name, string blobPath, string recognizedData, string recognizedSchema)
    {
        Name = name;
        BlobPath = blobPath;
        RecognizedData = recognizedData;
        RecognizedSchema = recognizedSchema;
        TouchUpdatedAt();
    }

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

    /// <summary>Пометить набор устаревшим — блоб заменён после распознавания (данные к прежнему файлу).</summary>
    public void MarkRecognitionStale()
    {
        RecognitionStale = true;
        TouchUpdatedAt();
    }
}
