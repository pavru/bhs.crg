using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Состояние коммерческого предложения (ТЗ PLAN-12). Отправка и итог — <c>plan-13</c>, PR 2.</summary>
public enum OfferStatus
{
    Draft,
    Sent,
    Accepted,
    Rejected,
    NoAnswer,
}

/// <summary>
/// Режим НДС у КП (решение В-9 (а), ТЗ PLAN-11): «в том числе», «сверху», «без НДС». Своё перечисление, а не
/// <c>VatMode</c> счёта: модуль <c>plan</c> на <c>costs</c> сослаться не может (<c>ModuleBoundaryTests</c>), а
/// третьего режима у счёта нет (D19 зонтика <c>plan-00-epic</c>).
/// </summary>
public enum OfferVatMode
{
    None,
    Included,
    OnTop,
}

/// <summary>
/// Коммерческое предложение — запись модуля (change <c>plan-13-offers</c>, ТЗ PLAN-10, PLAN-Q8, TYPE-17, D5
/// зонтика <c>plan-00-epic</c>). Делается из редакции сметы одним действием и фиксирует её (ТЗ PLAN-5).
///
/// <para><b>Почему не объект общей таблицы.</b> В КП цены продажи, а рядом — себестоимость сметы: в общей
/// таблице суммы увидел бы любой пользователь <c>id</c>, а здесь их открывает право <c>plan.offer.read</c>.</para>
///
/// <para><b>Колонками — всё, на что опирается код</b>: наценка, режим и ставка НДС, суммы. Схемой типа
/// <c>КоммерческоеПредложение</c> описаны только поля заказчика (<see cref="Data" />); системные «Наценка» и
/// «Итого» в <see cref="Data" /> не лежат никогда — охрана записи видит их проекцией из колонок
/// (<see cref="Offers.OfferRequisites" />).</para>
///
/// <para>⚠️ <b>Себестоимости здесь нет и не будет</b> (ТЗ PLAN-11, D4): её показывают из сметы, правом
/// <c>plan.estimate.cost</c>. Колонка себестоимости в КП была бы вторым местом, через которое она утекает.</para>
/// </summary>
public sealed class Offer
{
    public const int NumberLength = 32;
    public const int UserNameLength = 300;

    private Offer() { }

    public Guid Id { get; private set; }

    /// <summary>«КП-2026-014» — сквозной по экземпляру, по году даты КП; не меняется между версиями.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Год номера — часть ключа единственности вместе с <see cref="Sequence" />.</summary>
    public int Year { get; private set; }

    /// <summary>Порядковый номер в году.</summary>
    public int Sequence { get; private set; }

    public DateOnly Date { get; private set; }

    /// <summary>Тип записи — <c>КоммерческоеПредложение</c>: по его схеме проверяются поля заказчика.</summary>
    public Guid DocumentTypeId { get; private set; }

    /// <summary>Стройка — запись ядра, идентификатором (ТЗ CORE-34.1): стройка сметы-источника.</summary>
    public Guid ConstructionId { get; private set; }

    /// <summary>Заказчик — организация справочника по роли; <c>null</c> — у черновика ещё не выбран.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>Редакция-источник; <c>null</c> — КП по прайс-листу (<c>plan-14</c>).</summary>
    public Guid? RevisionId { get; private set; }

    /// <summary>На какую дату сняты цены. Правка шапки снимок цен не меняет.</summary>
    public DateOnly? PriceListDate { get; private set; }

    public OfferStatus Status { get; private set; }

    /// <summary>Номер действующей версии (ТЗ PLAN-12); прежние — историей <c>OfferVersion</c> (PR 2).</summary>
    public int CurrentVersion { get; private set; }

    /// <summary>Ручная дата отправки текущей версии; у нового черновика её нет.</summary>
    public DateOnly? SentOn { get; private set; }

    public OfferVatMode VatMode { get; private set; }

    /// <summary>Ставка НДС в процентах; <c>null</c> — режим «без НДС».</summary>
    public decimal? VatRate { get; private set; }

    /// <summary>Наценка предложения в процентах — уровень, действующий там, где у раздела и строки своей нет.</summary>
    public decimal Markup { get; private set; }

    /// <summary>Сумма строк по ценам продажи.</summary>
    public decimal LinesTotal { get; private set; }

    /// <summary>Сумма НДС, посчитанная с суммы строк; <c>null</c> — «без НДС».</summary>
    public decimal? VatAmount { get; private set; }

    /// <summary>«Итого» — сумма к оплате: при НДС сверху — с НДС, иначе сумма строк.</summary>
    public decimal Total { get; private set; }

    /// <summary>Поля заказчика по схеме типа — JSON-объект без системных полей.</summary>
    public string Data { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Кто сделал КП — справка, а не связь (удалению учётной записи не мешает).</summary>
    public Guid? CreatedBy { get; private set; }

    public string? CreatedByName { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public List<OfferLine> Lines { get; private set; } = [];

    public List<OfferSectionMarkup> SectionMarkups { get; private set; } = [];

    /// <summary>Номер словами — для журнала, отказов и фиксации редакции.</summary>
    public string Label => $"КП № {Number}";

    /// <summary>Номер по году и порядковому: «КП-2026-014».</summary>
    public static string NumberOf(int year, int sequence) => $"КП-{year}-{sequence:000}";

    internal static Offer Create(
        Guid documentTypeId, Guid constructionId, Guid? revisionId, DateOnly date, int sequence,
        Guid? createdBy, string? createdByName, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            Number = NumberOf(date.Year, sequence),
            Year = date.Year,
            Sequence = sequence,
            Date = date,
            DocumentTypeId = documentTypeId,
            ConstructionId = constructionId,
            RevisionId = revisionId,
            Status = OfferStatus.Draft,
            CurrentVersion = 1,
            VatMode = OfferVatMode.None,
            CreatedAt = now,
            CreatedBy = createdBy,
            CreatedByName = PlanText.Clip(createdByName, UserNameLength),
            ChangedAt = now,
        };

    /// <summary>Шапка: дата, заказчик, режим и ставка НДС. Суммы после неё пересчитывает <see cref="Reprice" />.</summary>
    internal void ReplaceHeader(DateOnly date, Guid? customerId, OfferVatMode vatMode, decimal? vatRate, DateTimeOffset now)
    {
        EnsureDraft();
        Date = date;
        CustomerId = customerId;
        VatMode = vatMode;
        VatRate = vatMode == OfferVatMode.None ? null : vatRate;
        ChangedAt = now;
    }

    /// <summary>
    /// Поля заказчика по схеме типа — ЕДИНСТВЕННЫЙ путь данных по схеме в КП (перепись
    /// <c>RecordWriteGuardCoverageTests</c>). Звать — только после охраны записи: системных полей здесь уже нет.
    /// </summary>
    internal void ApplyCustomerData(JsonElement data, DateTimeOffset now)
    {
        EnsureDraft();
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException("Поля заказчика КП — не JSON-объект.");
        Data = data.GetRawText();
        ChangedAt = now;
    }

    /// <summary>Наценка предложения — уровень по умолчанию для всех строк.</summary>
    internal void ChangeMarkup(decimal markup, DateTimeOffset now)
    {
        EnsureDraft();
        Markup = markup;
        ChangedAt = now;
    }

    /// <summary>Суммы КП — их кладёт только расчёт (<see cref="Offers.OfferPricing" />), руками не правятся.</summary>
    internal void Reprice(decimal linesTotal, decimal? vatAmount, decimal total)
    {
        LinesTotal = linesTotal;
        VatAmount = vatAmount;
        Total = total;
    }

    internal void SetPriceListDate(DateOnly date, DateTimeOffset now)
    {
        PriceListDate = date;
        ChangedAt = now;
    }

    internal void EnsureDraft()
    {
        if (Status != OfferStatus.Draft)
            throw new ConflictException("КП отправлено: правка — новой версией. Создайте новую версию КП.");
    }

    internal void Send(DateOnly date, DateTimeOffset now)
    {
        EnsureDraft();
        Status = OfferStatus.Sent;
        SentOn = date;
        ChangedAt = now;
    }

    internal void SetOutcome(OfferStatus status, DateTimeOffset now)
    {
        if (Status != OfferStatus.Sent)
            throw new ConflictException("Итог отмечается у отправленного КП. Перечитайте его состояние.");
        if (status is not (OfferStatus.Accepted or OfferStatus.Rejected or OfferStatus.NoAnswer))
            throw new InvalidRequestException("Итог КП — принято, отклонено или без ответа.");
        Status = status;
        ChangedAt = now;
    }

    internal void EnsureSent()
    {
        if (Status == OfferStatus.Draft)
            throw new ConflictException("КП ещё не отправлено — правьте этот черновик. Новая версия нужна после отправки.");
    }

    internal void BeginNextVersion(DateTimeOffset now)
    {
        EnsureSent();
        CurrentVersion++;
        Status = OfferStatus.Draft;
        SentOn = null;
        ChangedAt = now;
    }

    internal void EnsureDeletable()
    {
        if (Status != OfferStatus.Draft || CurrentVersion != 1 || SentOn is not null)
            throw new ConflictException("КП уже отправлялось — удалять его нельзя. Удаляется только черновик первой версии без отправок.");
    }
}
