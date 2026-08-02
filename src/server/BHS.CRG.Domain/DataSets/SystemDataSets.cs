namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Системные наборы данных: сырьё — не загруженный файл, а данные самой системы (документы
/// комплекта, общие данные, документы качества). Маркер вида консолидации хранится в
/// <c>DataSetSource.SheetOrPath</c> — для такого источника это поле не несёт смысла листа/XPath,
/// как и у PDF-проекций (см. <c>PdfProfiles</c>), и переиспользуется как служебная метка.
///
/// Маркер может быть параметризован (<c>system:objects:{typeId}</c> по образцу
/// <c>gost-table:{id}</c>) — часть после префикса разбирает сам провайдер.
/// </summary>
public static class SystemDataSets
{
    /// <summary>Колонка BlobPath — NOT NULL, а блоба у системного набора нет.</summary>
    public const string BlobPathSentinel = "system";

    /// <summary>Общий префикс всех маркеров консолидаций.</summary>
    public const string MarkerPrefix = "system:";

    /// <summary>Документы комплекта, в границах которого живёт набор.</summary>
    public const string SetDocumentsMarker = "system:set-documents";

    public static bool IsSystemMarker(string sheetOrPath) =>
        sheetOrPath.StartsWith(MarkerPrefix, StringComparison.Ordinal);
}
