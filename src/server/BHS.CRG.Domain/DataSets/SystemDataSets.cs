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

    /// <summary>Библиотека документов качества, видимых с уровня набора (цепочка вверх).</summary>
    public const string QualityDocumentsMarker = "system:quality-documents";

    /// <summary>Победившие связки «материал → документ качества» на уровне набора.</summary>
    public const string MaterialQualityMarker = "system:material-quality";

    /// <summary>Документы всех комплектов поддерева — реестр раздела или стройки.</summary>
    public const string SubtreeDocumentsMarker = "system:subtree-documents";

    /// <summary>
    /// Записи общих данных одного составного типа: маркер несёт идентификатор типа
    /// (<c>system:objects:{typeId}</c>). Типов много, и провайдер на каждый не заводится — часть
    /// после префикса разбирает сам провайдер, как у проекций таблиц ГОСТ.
    /// </summary>
    public const string ObjectsMarkerPrefix = "system:objects:";

    /// <summary>
    /// Таблица модуля (ТЗ CORE-33, CORE-24; задача G1b, issue #1089): маркер несёт адрес таблицы
    /// (<c>system:table:costs.invoices</c>). Поставщик заводится на каждую таблицу — у каждой своё
    /// объявление доступа.
    /// </summary>
    public const string TableMarkerPrefix = "system:table:";

    /// <summary>
    /// Колонки источника таблицы — параметром маркера (мост «Сделать источником данных», G3b, issue
    /// #1096): <c>system:table:costs.invoices?columns=Номер,Сумма</c>. Без параметра источник берёт все
    /// колонки таблицы, как и раньше. Параметр, а не своё поле источника: у системного источника маркер
    /// и есть его извлечение, и колонки — часть того же «что взять».
    /// </summary>
    public const string TableColumnsParameter = "?columns=";

    /// <summary>Маркер таблицы с колонками; пустой список колонок — маркер без параметра.</summary>
    public static string TableMarker(string address, IReadOnlyList<string>? columns) =>
        columns is not { Count: > 0 }
            ? TableMarkerPrefix + address
            : TableMarkerPrefix + address + TableColumnsParameter + string.Join(",", columns.Select(Escape));

    /// <summary>
    /// Адрес таблицы и колонки из маркера; false — маркер не таблицы. <paramref name="columns" /> null —
    /// параметра нет, брать все колонки.
    /// </summary>
    public static bool TryParseTableMarker(string marker, out string address, out IReadOnlyList<string>? columns)
    {
        address = "";
        columns = null;
        if (!marker.StartsWith(TableMarkerPrefix, StringComparison.Ordinal)) return false;
        var rest = marker[TableMarkerPrefix.Length..];
        var at = rest.IndexOf(TableColumnsParameter, StringComparison.Ordinal);
        if (at < 0)
        {
            address = rest;
            return rest.Length > 0;
        }
        address = rest[..at];
        var list = rest[(at + TableColumnsParameter.Length)..];
        columns = list.Length == 0 ? null : [.. list.Split(',').Select(Unescape)];
        return address.Length > 0;
    }

    // Ключ колонки может оказаться полем схемы типа — с любыми знаками. Запятая разделяет колонки, а
    // процент — сам знак экранирования; остальное идёт как есть, чтобы маркер оставался читаемым.
    private static string Escape(string key) => key.Replace("%", "%25").Replace(",", "%2C");
    private static string Unescape(string key) => key.Replace("%2C", ",").Replace("%25", "%");

    /// <summary>Идентификатор типа из маркера общих данных; false — маркер не тот или не разбирается.</summary>
    public static bool TryParseObjectsMarker(string marker, out Guid typeId)
    {
        typeId = Guid.Empty;
        return marker.StartsWith(ObjectsMarkerPrefix, StringComparison.Ordinal)
            && Guid.TryParse(marker[ObjectsMarkerPrefix.Length..], out typeId);
    }

    public static bool IsSystemMarker(string sheetOrPath) =>
        sheetOrPath.StartsWith(MarkerPrefix, StringComparison.Ordinal);
}
