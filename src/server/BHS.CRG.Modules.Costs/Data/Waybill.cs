namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Состояние накладной (ТЗ COST-9, COST-17).</summary>
public enum WaybillState
{
    /// <summary>Вводится: в «материалы на объекте» не попадает ни одной строкой.</summary>
    Draft,

    /// <summary>Проведена: перечисленные материалы выданы на объект.</summary>
    Posted,
}

/// <summary>
/// Расходная накладная — отпуск материалов со своего склада на стройку (задача D1 этапа 2,
/// issue #1083; ТЗ COST-5, COST-17).
///
/// <para><b>Денег в накладной нет вовсе</b> — ни цены, ни суммы, и это не упрощение первой версии.
/// Право на накладные не открывает счетов (COST-29), а «материалы на объекте» читают монтажник и
/// инженер исполнительной документации (COST-18): сумма в строке накладной была бы суммой, показанной
/// мимо <c>costs.invoice.read</c>. Сколько материал стоил, знает счёт.</para>
///
/// <para><b>Колонки, а не тип со схемой.</b> Счёт — тип записи с расширяемой схемой: к нему заказчик
/// дописывает свои поля, и у него есть печать. Накладная целиком состоит из того, на что опирается код
/// (правило CORE-15): по стройке и дате строится перечень отпущенного, по состоянию решается, считать
/// ли строку. Своих полей у неё пока никто не просил; понадобятся — приедет типом, и колонки останутся
/// колонками.</para>
///
/// <para>⚠️ <b>Стройка — идентификатором, без внешнего ключа</b>, как поставщик у счёта: стройка живёт
/// в ядре, а схема модуля от схемы ядра ключами не связывается. «Ссылка есть, стройки нет»
/// показывается человеку, а не прячется.</para>
/// </summary>
public sealed class Waybill
{
    /// <summary>Пределы длины — те же числа, что у колонок базы (см. <see cref="Invoice.NumberLength"/>).</summary>
    public const int NumberLength = 100;

    /// <inheritdoc cref="NumberLength"/>
    public const int NameLength = 200;

    /// <summary>Для EF.</summary>
    private Waybill() { }

    public Guid Id { get; private set; }

    public string? Number { get; private set; }

    /// <summary>Дата отпуска — по ней материал считается выданным на объект.</summary>
    public DateOnly? IssuedOn { get; private set; }

    /// <summary>
    /// Склад — названием, как он стоит в бумаге. Справочника складов в системе нет, и заводить его
    /// ради одного поля рано: склад ведётся в 1С (COST-4.1), оттуда и приедет его имя.
    /// </summary>
    public string? Warehouse { get; private set; }

    /// <summary>Получатель: стройка, на которую отпущено.</summary>
    public Guid? ConstructionId { get; private set; }

    /// <summary>Кто принял — словами из бумаги: прораб, бригадир. Не учётная запись.</summary>
    public string? ReceivedBy { get; private set; }

    public string? Note { get; private set; }

    public WaybillState State { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public Guid? PostedBy { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Waybill Create(Guid? author) => new()
    {
        Id = Guid.CreateVersion7(),
        State = WaybillState.Draft,
        CreatedBy = author,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public WaybillHeader Header() => new(Number, IssuedOn, Warehouse, ConstructionId, ReceivedBy, Note);

    public void Apply(WaybillHeader header)
    {
        Number = header.Number;
        IssuedOn = header.IssuedOn;
        Warehouse = header.Warehouse;
        ConstructionId = header.ConstructionId;
        ReceivedBy = header.ReceivedBy;
        Note = header.Note;
        Touch();
    }

    public void Post(Guid? by)
    {
        State = WaybillState.Posted;
        PostedAt = DateTimeOffset.UtcNow;
        PostedBy = by;
        Touch();
    }

    public void ReturnToDraft()
    {
        State = WaybillState.Draft;
        PostedAt = null;
        PostedBy = null;
        Touch();
    }

    /// <summary>
    /// Строки изменились — меняется и накладная: её версия строки защищает набор строк от
    /// одновременной правки, а своей версии у строк нет.
    /// </summary>
    public void ContentChanged() => Touch();

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>Шапка накладной — то, что правит человек.</summary>
public sealed record WaybillHeader(
    string? Number,
    DateOnly? IssuedOn,
    string? Warehouse,
    Guid? ConstructionId,
    string? ReceivedBy,
    string? Note);

/// <summary>
/// Строка накладной. Та же модель, что у строки счёта (задача C2), без денег: наименование из бумаги
/// остаётся цитатой, позиция номенклатуры — ссылкой, и строка без ссылки «не сопоставлена».
/// </summary>
public sealed class WaybillLine
{
    /// <inheritdoc cref="InvoiceLine.UnitLength"/>
    public const int UnitLength = InvoiceLine.UnitLength;

    /// <summary>Для EF.</summary>
    private WaybillLine() { }

    public Guid Id { get; private set; }

    public Guid WaybillId { get; private set; }

    /// <summary>Порядок строки в бумаге.</summary>
    public int Ordinal { get; private set; }

    /// <summary>
    /// Позиция номенклатуры. Пусто — строка не сопоставлена: в «материалы на объекте» она не попадает
    /// и считается в «не сопоставлено» (ТЗ COST-17).
    /// </summary>
    public Guid? NomenclatureId { get; private set; }

    /// <summary>Наименование, как оно стоит в накладной.</summary>
    public string? SourceText { get; private set; }

    public string? Unit { get; private set; }

    /// <summary>Количество. Отрицательное — возврат со стройки (ТЗ COST-19).</summary>
    public decimal? Quantity { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WaybillLine Create(Guid waybillId) => new()
    {
        Id = Guid.CreateVersion7(),
        WaybillId = waybillId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public WaybillLineValues Snapshot() => new(NomenclatureId, SourceText, Unit, Quantity, Note);

    public void Apply(int ordinal, WaybillLineValues values)
    {
        Ordinal = ordinal;
        NomenclatureId = values.NomenclatureId;
        SourceText = values.SourceText;
        Unit = values.Unit;
        Quantity = values.Quantity;
        Note = values.Note;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Сопоставление — единственная правка строки, разрешённая проведённой накладной: накладная из 1С
    /// приходит проведённой и с несопоставленными строками, и свести их со справочником надо, не
    /// распроводя документ.
    /// </summary>
    public void Match(Guid? nomenclatureId)
    {
        NomenclatureId = nomenclatureId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record WaybillLineValues(
    Guid? NomenclatureId,
    string? SourceText,
    string? Unit,
    decimal? Quantity,
    string? Note);
