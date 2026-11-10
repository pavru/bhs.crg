using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Приложение к смете в списке (change <c>plan-11-conjuncture</c>): конъюнктурный анализ или ведомость объёмов —
/// файл, версии и реквизиты файла. Денег в заголовке нет — они у позиций анализа.
/// </summary>
/// <param name="Kind"><c>ka</c> — конъюнктурный анализ, <c>vor</c> — ведомость объёмов работ.</param>
/// <param name="Parsed">Разобрано позиций (у ВОР — работ): приложено могло быть с согласия без не разобранных.</param>
/// <param name="InFile">Позиций в файле.</param>
/// <param name="CurrentPriceDate">Дата текущих цен — у анализа.</param>
/// <param name="Chapters">Глав среди разобранного — для строки списка «48 работ в 7 главах».</param>
/// <param name="Suppliers">Поставщиков в предложениях анализа (по ИНН, без ИНН — по имени); у ВОР — <c>null</c>.</param>
public sealed record EstimateAttachmentDto(
    Guid Id, string Kind, Guid EstimateId, string FileName, string? Number, string? Name, string? ProgramVersion,
    string? SchemaVersion, DateOnly? CreationDate, DateOnly? CurrentPriceDate, int Parsed, int InFile, int Chapters,
    int? Suppliers, DateTimeOffset ImportedAt, string? ImportedBy, string Version);

/// <summary>Принятие отчёта разбора: <c>acceptPartial</c> — согласие приложить без не разобранных позиций.</summary>
public sealed record EstimateAttachRequest(bool AcceptPartial = false);

/// <summary>Приложение заведено; <c>repeated</c> — заведено раньше тем же импортом, второго нет.</summary>
public sealed record EstimateAttachedDto(EstimateAttachmentDto Attachment, bool Repeated);

/// <summary>Выбранная цена позиции — только с <c>plan.estimate.cost</c>.</summary>
public sealed record ConjunctureItemCostDto(decimal? SelectedPrice);

/// <summary>Цены предложения — только с <c>plan.estimate.cost</c>.</summary>
/// <param name="Price">Отпускная цена без НДС.</param>
public sealed record ConjunctureOfferCostDto(decimal? Price, decimal? PriceWithVat);

/// <summary>Коммерческое предложение позиции: поставщик текстом из файла, ставка НДС и дата — без права видны.</summary>
/// <param name="Order">Номер по порядку у позиции, с единицы.</param>
/// <param name="Selected">Это предложение дало выбранную цену позиции.</param>
public sealed record ConjunctureOfferDto(
    int Order, string Number, string Caption, string? Units, string? ResourceCode, string? Supplier, string? SupplierInn,
    decimal? Vat, DateOnly? Date, bool Selected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConjunctureOfferCostDto? Cost);

/// <summary>Позиция анализа с предложениями поставщиков.</summary>
/// <param name="Selection"><c>min</c> — минимальная из предложений, <c>chosen</c> — выбрана в файле, <c>none</c> — цены
/// нет ни у одного предложения.</param>
/// <param name="SelectedOffer">Номер выбранного предложения (<see cref="ConjunctureOfferDto.Order" />).</param>
/// <param name="Inactive">В анализе не учитывается.</param>
public sealed record ConjunctureItemDto(
    Guid Id, int Order, int ChapterIndex, string ChapterCaption, string Number, string Caption, string? Units,
    bool Inactive, string Selection, int? SelectedOffer, IReadOnlyList<ConjunctureOfferDto> Offers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConjunctureItemCostDto? Cost);

/// <summary>Страница позиций анализа: <c>total</c> — позиций всего; без права на стоимость — <c>hidden</c>.</summary>
public sealed record ConjunctureItemsDto(
    int Total, int Offset, IReadOnlyList<ConjunctureItemDto> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <summary>Строка ведомости объёмов: обоснование — шифр, ссылка на чертежи и примечание сметчика.</summary>
/// <param name="ParentNumber">У подчинённой работы — номер вышестоящей («входит в № 12»): ближайшая не подчинённая
/// выше в той же главе — так их пишет ГРАНД-Смета (<c>SlaveRow</c> идёт за своей работой). Выше в главе нет — <c>null</c>.</param>
public sealed record VolumeLineDto(
    Guid Id, int Order, int ChapterIndex, string ChapterCaption, string Number, string Caption, string? Code,
    string? Units, decimal Quantity, string? Formula, string? Drawings, string? Note, bool Subordinate,
    string? ParentNumber);

public sealed record VolumeLinesDto(int Total, int Offset, IReadOnlyList<VolumeLineDto> Lines);
