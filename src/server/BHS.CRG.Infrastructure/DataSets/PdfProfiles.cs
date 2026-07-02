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
}
