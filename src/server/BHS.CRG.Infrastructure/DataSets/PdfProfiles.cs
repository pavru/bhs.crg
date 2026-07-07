namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Профили распознавания PDF-источников и маркеры, которыми они помечаются в
/// <c>DataSetSource.SheetOrPath</c> (для PDF это поле не несёт смысла XPath/JSONPath —
/// переиспользуется как служебная метка, без миграции схемы; см. также константу
/// "titleblock-registry" в DataSetService).
/// </summary>
public static class PdfProfiles
{
    /// <summary>Профиль, выбираемый при создании источника (см. CreatePdfSourceInput.Profile).</summary>
    public const string GostTitleBlock = "gost-titleblock";
    public const string Invoice = "invoice";

    /// <summary>Маркеры конкретных DataSetSource внутри пары «Счёт на оплату».</summary>
    public const string InvoiceHeaderMarker = "invoice-header";
    public const string InvoiceLineItemsMarker = "invoice-lineitems";

    /// <summary>Маркеры тройки источников профиля "gost-titleblock" (созданных с этого момента —
    /// legacy-источники с маркером "titleblock-registry" остаются постраничным плоским реестром,
    /// не трогаем их поведение).</summary>
    public const string GostCoverMarker = "gost-cover";
    public const string GostTitlePageMarker = "gost-titlepage";
    public const string GostDocumentsMarker = "gost-documents";

    /// <summary>Префикс маркера динамического табличного источника документа (спецификация/кабельный
    /// журнал): <c>gost-table:{каноническая первая страница документа}</c>. Это детерминированная
    /// ПРОЕКЦИЯ группы-документа — инвалидируется при ре-группировке (см. ReprojectTableSourcesAsync).</summary>
    public const string GostTableMarkerPrefix = "gost-table:";

    /// <summary>Legacy-маркер плоского постраничного реестра (до тройки обложка/титул/документы).</summary>
    public const string LegacyTitleBlockRegistryMarker = "titleblock-registry";

    /// <summary>Источник, наполняемый распознаванием (vision-LLM), а не детерминированным парсером:
    /// <see cref="PdfDataSetParser"/> такие НЕ детектит. При замене файла их нельзя удалять как
    /// «отсутствующие в файле» — данные приходят из распознавания, а не из структуры файла.</summary>
    public static bool IsRecognitionMarker(string sheetOrPath) =>
        sheetOrPath is InvoiceHeaderMarker or InvoiceLineItemsMarker
            or GostCoverMarker or GostTitlePageMarker or GostDocumentsMarker
            or LegacyTitleBlockRegistryMarker
        || sheetOrPath.StartsWith(GostTableMarkerPrefix, StringComparison.Ordinal);
}
