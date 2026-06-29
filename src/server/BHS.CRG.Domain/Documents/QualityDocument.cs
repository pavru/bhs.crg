using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>Источник появления документа качества в библиотеке.</summary>
public enum QualityDocSource { Manual = 0, Fgis = 1, Manufacturer = 2, Web = 3 }

/// <summary>
/// Документ, подтверждающий качество (сертификат/декларация/паспорт), в ОБЩЕЙ переиспользуемой
/// библиотеке — вне конкретного комплекта. Хранит реквизиты (как у DocumentInstance) и скан.
/// Связывается с материалами через <see cref="MaterialQualityLink"/>.
/// </summary>
public class QualityDocument : Entity
{
    /// <summary>Подтип документа (Сертификат соответствия / Декларация / …).</summary>
    public Guid DocumentTypeId { get; private set; }

    public string DisplayName { get; private set; } = null!;

    /// <summary>Реквизиты документа (тот же формат, что DocumentInstance.Requisites).</summary>
    public JsonDocument Requisites { get; private set; } = null!;

    public string? ScanBlobPath { get; private set; }
    public string? ScanFileName { get; private set; }
    public string? ScanMimeType { get; private set; }

    public QualityDocSource Source { get; private set; }

    /// <summary>URL-источник (для импортированных из веба) — для дедупликации повторного импорта.</summary>
    public string? SourceUrl { get; private set; }

    /// <summary>Область видимости для переиспользования (System / Construction / …).</summary>
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    private QualityDocument() { }

    public static QualityDocument Create(
        Guid documentTypeId, string displayName, JsonDocument requisites,
        CatalogScope scope, Guid? scopeId, QualityDocSource source, string? sourceUrl = null)
        => new()
        {
            DocumentTypeId = documentTypeId,
            DisplayName = displayName,
            Requisites = requisites,
            Scope = scope,
            ScopeId = scopeId,
            Source = source,
            SourceUrl = sourceUrl,
        };

    public void Update(Guid documentTypeId, string displayName, JsonDocument requisites)
    {
        DocumentTypeId = documentTypeId;
        DisplayName = displayName;
        Requisites = requisites;
        TouchUpdatedAt();
    }

    public void SetScan(string? blobPath, string? fileName, string? mimeType)
    {
        ScanBlobPath = blobPath;
        ScanFileName = fileName;
        ScanMimeType = mimeType;
        TouchUpdatedAt();
    }
}
