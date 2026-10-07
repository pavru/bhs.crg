using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Заводские профили распознавания, которыми владеет ЯДРО (issue #1075).
///
/// <para>⚠️ «Счёт на оплату» здесь — отступление от ТЗ, принятое владельцем продукта 07.10.2026.
/// <c>CORE-Q6</c> отдаёт его модулю счетов, и туда он переедет вместе с задачей B1b, когда у модуля
/// появится потребитель: путь «скан → черновик счёта». Сегодня потребитель один — PDF-источник набора
/// данных, а наборы данных принадлежат ядру. Объяви профиль модуль счетов, он пропал бы у каждой
/// действующей установки: по умолчанию включён только модуль исполнительной документации.</para>
///
/// <para>Собирается ИЗ СУЩЕСТВУЮЩИХ КОНСТАНТ (<see cref="InvoiceFields" />), а не переписывается рядом
/// — поэтому сидированный профиль даёт побуквенно тот же запрос, что и до появления профилей.</para>
///
/// <para>Один профиль — ОДИН вызов распознавания: шапка и товары счёта лежат в одном профиле
/// (поля и колонки), потому что запрос к модели единый. Служебное в профиль не входит: поле-массив
/// «Товары» — форма ответа, его подмешивает код.</para>
/// </summary>
public static class CoreRecognitionProfiles
{
    /// <summary>Стабильный код — ключ строки в базе, не переименовывается.</summary>
    public const string InvoiceCode = "invoice";

    public static IReadOnlyList<RecognitionProfileDeclaration> All =>
    [
        new(InvoiceCode, "Счёт на оплату", RecognitionProfileKind.Invoice,
            Map(InvoiceFields.HeaderFields), Map(InvoiceFields.LineItemColumns), Shape: null,
            Owner: RecognitionProfileCatalog.CoreOwner),
    ];

    private static IReadOnlyList<RecognitionProfileField> Map(IReadOnlyList<RecognitionField> fields) =>
        [.. fields.Select(f => new RecognitionProfileField(f.Path, f.Title, f.Type, f.Options))];
}
