using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Формат сырья набора. <see cref="System"/> — не файл: строки поставляет провайдер, читающий
/// данные самой системы (документы комплекта и т.п.), блоба у такого набора нет.
/// </summary>
public enum DataSetFormat { Csv, Xlsx, Xls, Xml, Json, Zip, Pdf, System }

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

    /// <summary>Сырьё профиля «Счёт на оплату» (issue #44) — JSON {Header, LineItems}, аналог
    /// <see cref="Grouping"/> для ГОСТ (иная, непостраничная форма — своя колонка, не обобщение).
    /// Пишется распознаванием; источники «Шапка»/«Товары» — кандидаты, проецируются пользователем.</summary>
    public string? InvoiceRawData { get; private set; }

    /// <summary>
    /// Профили распознавания, привязанные к НАБОРУ (issue #412) — JSON-карта {вид: id профиля}, напр.
    /// <c>{"TitleBlock":"…","CoverTitle":"…"}</c>. Нужна для не-табличных видов, которые работают на
    /// уровне файла целиком (штамп, обложка/титул, счёт), — в отличие от таблиц, где профиль
    /// привязывается к конкретной группе листов (issue #410).
    ///
    /// <see cref="PreprocessingProfile"/> при этом остаётся: он выбирает СЦЕНАРИЙ («ГОСТ-альбом» или
    /// «счёт»), а эта карта — ПАРАМЕТРЫ применяемых в сценарии промптов. Null/нет ключа — берётся
    /// встроенный профиль вида.
    /// </summary>
    public string? RecognitionProfiles { get; private set; }

    private DataSetFile() { }

    public static DataSetFile Create(string name, DataSetFormat format, string blobPath,
        CatalogScope scope, Guid? scopeId)
        => new() { Name = name, Format = format, BlobPath = blobPath, Scope = scope, ScopeId = scopeId };

    /// <summary>
    /// Системный набор: сырьё — не файл, а данные самой системы в границах scope. Блоба нет,
    /// BlobPath несёт сентинел (колонка NOT NULL). Источники такого набора — консолидации-провайдеры.
    /// </summary>
    public static DataSetFile CreateSystem(string name, CatalogScope scope, Guid? scopeId)
        => new()
        {
            Name = name,
            Format = DataSetFormat.System,
            BlobPath = SystemDataSets.BlobPathSentinel,
            Scope = scope,
            ScopeId = scopeId,
        };

    /// <summary>Набор без файла: сырьё — данные системы (см. <see cref="CreateSystem"/>).</summary>
    public bool IsSystem => Format == DataSetFormat.System;

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

    /// <summary>Задать карту профилей распознавания набора (issue #412). Пустая карта → null,
    /// чтобы «нет привязок» имело единственное представление.</summary>
    public void SetRecognitionProfiles(string? profilesJson)
    {
        RecognitionProfiles = string.IsNullOrWhiteSpace(profilesJson) || profilesJson.Trim() == "{}"
            ? null : profilesJson;
        TouchUpdatedAt();
    }

    /// <summary>Задать/обновить авторитетную группировку набора (JSON GostGroupingData).
    /// Устаревание снимается НЕ здесь, а на источниках свежим кэшем (issue #815): группировку пишут и
    /// смена тэгов, и привязка профиля, и ручная правка разбиения — то есть пути, где ничего не
    /// распознавали. Сбрасывая признак тут, мы объявляли бы данные свежими после переименования тэга.</summary>
    public void SetGrouping(string? groupingJson)
    {
        Grouping = groupingJson;
        TouchUpdatedAt();
    }

    /// <summary>Пишет сырьё профиля «Счёт на оплату» (issue #44) — аналог SetGrouping для ГОСТ.</summary>
    public void SetInvoiceRawData(string? rawDataJson)
    {
        InvoiceRawData = rawDataJson;
        TouchUpdatedAt();
    }

}
