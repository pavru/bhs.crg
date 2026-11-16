using System.Text.Json;
using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>«Сделать КП из редакции» (ТЗ PLAN-10): одно действие — строки редакции и наценка предложения.</summary>
/// <param name="Date">Дата КП; не названа — «сегодня» по поясу компании. По её году выдаётся номер.</param>
/// <param name="Markup">Наценка предложения в процентах; не названа — 0.</param>
/// <param name="VatMode"><c>included</c>, <c>on-top</c> или <c>none</c>; не назван — <c>none</c>.</param>
/// <param name="Data">Поля заказчика по схеме типа; системные «Наценка» и «Итого» не присылаются.</param>
public sealed record OfferCreateRequest(
    Guid RevisionId, Guid? CustomerId, DateOnly? Date, decimal? Markup, string? VatMode, decimal? VatRate,
    JsonElement? Data);

/// <summary>Шапка КП целиком: что не названо, то пусто (заказчик, ставка, поля заказчика).</summary>
public sealed record OfferUpdateRequest(
    DateOnly? Date, Guid? CustomerId, string? VatMode, decimal? VatRate, JsonElement? Data);

/// <summary>Наценка раздела ЛСР.</summary>
public sealed record OfferSectionMarkupRequest(string Code, decimal Markup);

/// <summary>Своя наценка строки.</summary>
public sealed record OfferLineMarkupRequest(Guid LineId, decimal Markup);

/// <summary>
/// Наценки КП целиком (ТЗ PLAN-11): предложения, разделов и строк. Не названный раздел или строка — без своей
/// наценки, у них действует уровень выше.
/// </summary>
public sealed record OfferMarkupsRequest(
    decimal Markup, IReadOnlyList<OfferSectionMarkupRequest>? Sections, IReadOnlyList<OfferLineMarkupRequest>? Lines);

/// <summary>Заказчик КП. <c>Lost</c> — записи в справочнике больше нет: ссылка есть, а назвать её нечем.</summary>
public sealed record OfferCustomerDto(Guid Id, string? Name, bool Archived, bool Lost);

/// <summary>Редакция-источник: смета, номер редакции.</summary>
public sealed record OfferRevisionDto(Guid Id, int Number, Guid EstimateId, string EstimateName);

/// <param name="Mode"><c>included</c> — НДС в том числе, <c>on-top</c> — сверху, <c>none</c> — без НДС.</param>
/// <param name="Rate">Ставка в процентах; <c>null</c> — «без НДС».</param>
public sealed record OfferVatDto(string Mode, decimal? Rate);

/// <param name="LinesTotal">Сумма строк по ценам продажи.</param>
/// <param name="Vat">НДС с суммы строк; <c>null</c> — «без НДС».</param>
/// <param name="Total">«Итого» — к оплате.</param>
public sealed record OfferMoneyDto(decimal LinesTotal, decimal? Vat, decimal Total);

/// <summary>Действующая наценка и откуда она: <c>line</c>, <c>section</c>, <c>offer</c>.</summary>
public sealed record OfferMarkupDto(decimal Value, string Level);

/// <summary>Раздел ЛСР в КП: строк в нём и его наценка (<c>null</c> — своей нет).</summary>
public sealed record OfferSectionDto(string Code, string? Name, int Lines, decimal? Markup);

/// <summary>
/// Себестоимость строки из сметы и маржа — только с <c>plan.estimate.cost</c> (ТЗ PLAN-11, D4). В таблицах КП
/// её нет: это итог строки редакции, прочитанный при ответе.
/// </summary>
/// <param name="Total">Итог строки ЛСР в текущих ценах с НР и СП — база наценки; <c>null</c> — итога в смете нет.</param>
/// <param name="Margin">Сумма строки минус база; <c>null</c> — базы нет.</param>
public sealed record OfferCostDto(decimal? Total, decimal? Margin);

/// <param name="OwnMarkup">Своя наценка строки; <c>null</c> — действует уровень выше.</param>
/// <param name="Price">Цена продажи за единицу; <c>null</c> — у строки нет объёма или итога в смете (пометка).</param>
public sealed record OfferLineDto(
    Guid Id, int Order, string? Number, LsrSectionDto? LsrSection, string Name, decimal? Volume, string? Unit,
    Guid? WorkTypeId, string Kind, decimal? OwnMarkup, OfferMarkupDto Markup, decimal? Price, decimal Amount,
    IReadOnlyList<PlanMark> Marks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OfferCostDto? Cost);

/// <param name="Version">Версия строки для <c>If-Match</c>.</param>
/// <param name="CurrentVersion">Номер версии КП (ТЗ PLAN-12).</param>
/// <param name="Status"><c>draft</c> — черновик.</param>
/// <param name="Hidden">Что скрыто правами (себестоимость и маржа); <c>null</c> — ничего.</param>
public sealed record OfferDto(
    Guid Id, string Number, DateOnly Date, string Status, int CurrentVersion, string Version, Guid ConstructionId,
    OfferCustomerDto? Customer, OfferRevisionDto? Revision, OfferVatDto Vat, decimal Markup,
    IReadOnlyList<OfferSectionDto> Sections, OfferMoneyDto Money, IReadOnlyList<OfferLineDto> Lines, JsonElement Data,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset ChangedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OfferCostDto? Cost,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <summary>КП в списке: шапка и суммы, без строк.</summary>
public sealed record OfferListItemDto(
    Guid Id, string Number, DateOnly Date, string Status, int CurrentVersion, string Version, Guid ConstructionId,
    OfferCustomerDto? Customer, OfferRevisionDto? Revision, OfferVatDto Vat, OfferMoneyDto Money, DateTimeOffset ChangedAt);

/// <param name="Next">Курсор следующей страницы; <c>null</c> — КП больше нет.</param>
public sealed record OfferListDto(
    IReadOnlyList<OfferListItemDto> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next);
