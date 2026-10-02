using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Ось «документ» состояния счёта (ТЗ COST-9). Двигает её человек.</summary>
public enum InvoiceState
{
    /// <summary>Приехал и живёт: строк может не быть вовсе, а срок и оплата уже работают.</summary>
    Draft,

    /// <summary>Строки есть, у всех номенклатура, баланс разноски сходится.</summary>
    Parsed,

    /// <summary>Не платим.</summary>
    Rejected,
}

/// <summary>
/// Ось «оплата» (ТЗ COST-9). Двигает её отметка платежа (C5, issue #1082).
///
/// ⚠️ «Просрочен» здесь НЕТ нарочно: это признак, который считается по дате и состоянию оплаты, а не
/// состояние, которое кто-то ставит. Храни мы его значением — он был бы верен ровно до полуночи.
/// </summary>
public enum InvoicePaymentState
{
    Unpaid,
    Partial,
    Paid,
}

/// <summary>
/// Счёт на оплату — <b>запись модуля</b> (задача C1 этапа 2, issue #1076, ТЗ CORE-16, CORE-20.1,
/// COST-6, COST-9): колонки-скелет в таблице модуля плюс остальное по схеме типа.
///
/// <para><b>Почему не объект общей таблицы.</b> В ней суммы счёта увидел бы любой пользователь
/// модуля исполнительной документации — через «Общие данные», MCP, поиск сопоставления и индекс
/// ссылок, — и ни одной ошибки при этом не случилось бы (ТЗ COST-29, решение 18.09.2026). Изоляция
/// здесь не пометка на типе, а место хранения.</para>
///
/// <para><b>Граница «колонка или схема» — правило ТЗ CORE-15</b> («на что опирается код»). Колонками
/// стало то, на что опирается код: дубликат ищется по поставщику, номеру и дате; реестр отбирается
/// по плательщику, состоянию, сроку; суммы сверяются с разноской и строками. Проверка простая:
/// «администратор завтра переименует это поле — что сломается?». Основание переименовать можно
/// безнаказанно, поэтому оно живёт в <see cref="Data" /> вместе с полями, которые заказчик дописал
/// сам.</para>
///
/// <para>⚠️ <b>Ключ, за которым стоит колонка, в <see cref="Data" /> не попадает никогда</b> — иначе
/// у значения стало бы два источника, и расходились бы они молча. Перекладывание в обе стороны
/// делает <c>InvoiceRequisites</c>, и там же это правило проверяется.</para>
///
/// <para>⚠️ <b>Ссылки на ядро — идентификатором, без внешнего ключа.</b> Схема модуля и схема ядра
/// живут в одной базе, и внешний ключ между ними технически возможен — но он связал бы миграции
/// двух наборов и выключение модуля: удалить организацию стало бы нельзя, пока цел счёт, о котором
/// ядро не знает. Целость ссылки проверяется чтением (<c>IModuleCatalog</c>), а копия и
/// восстановление их сохраняют (A2b, issue #1073).</para>
/// </summary>
public sealed class Invoice
{
    /// <summary>
    /// Предел длины номера — тот же, что у колонки базы, и живёт ОДНИМ числом нарочно.
    ///
    /// <para>⚠️ Иначе проверка и колонка расходятся молча, а расхождение выходит пятисотым: перебор
    /// длины ловит PostgreSQL (22001), уже внутри <c>SaveChangesAsync</c>, а там доменных отказов не
    /// бывает — наружу уезжает «внутренняя ошибка сервера» без имени поля. Наступали: номер в 140
    /// знаков давал 500 вместо отказа с подписью поля.</para>
    /// </summary>
    public const int NumberLength = 100;

    /// <summary>Для EF.</summary>
    private Invoice() { }

    public Guid Id { get; private set; }

    /// <summary>
    /// Тип счёта — тот, что модуль объявил, а ядро спроецировало (<c>CostsRecordTypes.Invoice</c>).
    /// Хранится у каждой записи, а не берётся по коду на чтении: по нему печать, тэги и форма
    /// находят схему, а код типа администратор переименовать не может, но подменить тип в системе —
    /// вполне (завёл, удалил, завёл заново). Запись обязана помнить свой.
    /// </summary>
    public Guid DocumentTypeId { get; private set; }

    public string? Number { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    /// <summary>Поставщик — запись справочника организаций ЯДРА.</summary>
    public Guid? SupplierId { get; private set; }

    /// <summary>Плательщик — своя организация; у компании их несколько (ТЗ COST-6).</summary>
    public Guid? PayerId { get; private set; }

    /// <summary>Назначение счёта (ТЗ COST-6.1): «что куплено и для чего» у документа целиком.</summary>
    public string? Purpose { get; private set; }

    /// <summary>Сумма к оплате — та, что стоит в бумаге поставщика (тэг <c>doc.total</c>).</summary>
    public decimal? Total { get; private set; }

    public decimal? VatTotal { get; private set; }

    public DateOnly? ShippedOn { get; private set; }

    public int? DeferralDays { get; private set; }

    /// <summary>
    /// Срок «оплатить до» (ТЗ COST-9.1). Колонка с правилом подстановки, а не расчётное поле:
    /// расчётное перекрыть нельзя, а этот срок человек правит руками.
    ///
    /// <para>⚠️ Здесь только хранение: само правило («дата отгрузки + отсрочка − 1») задаётся
    /// настройкой модуля, которой ещё нет (M1, issue #1070), и подставляет срок задача C4
    /// (issue #1080). Без даты отгрузки срок не определён — и это <c>null</c>, а не выдуманная
    /// дата.</para>
    /// </summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>Срок правил человек — правило подстановки его больше не трогает (ТЗ COST-9.1).</summary>
    public bool DueDateManual { get; private set; }

    public InvoiceState State { get; private set; }

    public InvoicePaymentState Payment { get; private set; }

    public string? ScanBlobPath { get; private set; }

    public string? ScanFileName { get; private set; }

    public string? ScanMimeType { get; private set; }

    /// <summary>
    /// Размер скана в байтах. Колонкой, а не вопросом к хранилищу: размер едет в значении файлового
    /// поля, значение собирается на каждое чтение — и на чтении реестра это был бы запрос к хранилищу
    /// на каждую строку. Ставится вместе с путём и без пути не бывает.
    /// </summary>
    public long? ScanSize { get; private set; }

    /// <summary>
    /// Ключи полей, которые заполнило распознавание и человек ещё не подтвердил (решение владельца
    /// продукта 29.09.2026, вариант C).
    ///
    /// <para><b>Почему на сервере, а не в состоянии формы.</b> Черновик создаёт ФОНОВАЯ задача
    /// (B1b, D4), а человек открывает его потом — иногда через день. Метка, живущая до перезагрузки
    /// страницы, в этом единственном сценарии, ради которого она и заводится, не существует
    /// вовсе.</para>
    ///
    /// <para><b>Конец у метки есть, и он двойной:</b> правка поля снимает метку с этого поля,
    /// «Всё верно» — со всего блока формы. Только правки было бы мало: правильно распознанное поле
    /// не трогают никогда, и метка осталась бы навсегда — то есть стала бы фоном. Сохранение метку
    /// НЕ снимает: <c>D4</c> сохраняет черновик до того, как его увидел человек.</para>
    ///
    /// <para>⚠️ Хранится ОДНА ось — «не подтверждено», та, по которой человек действует. «Откуда
    /// пришло навсегда» (<c>DataOrigin</c> у наборов данных) — другая ось, и в первой версии её не
    /// хранят: этого никто не просил, а журнал действий записывает и так.</para>
    /// </summary>
    public IReadOnlyList<string> Unconfirmed
    {
        get => unconfirmed;
        private set => unconfirmed = [.. value];
    }

    private List<string> unconfirmed = [];

    /// <summary>
    /// Остальные поля по схеме типа: основание и то, что заказчик дописал сам (уровень схемы —
    /// «расширяемый»). Колонок здесь нет — см. правило в описании класса.
    /// </summary>
    public JsonDocument Data { get; private set; } = JsonDocument.Parse("{}");

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Новый счёт. Всегда черновик и всегда неоплаченный: другого начала у счёта не бывает, а
    /// параметр с одним осмысленным значением приглашает передать второе.
    /// </summary>
    public static Invoice Create(Guid documentTypeId, Guid? author) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentTypeId = documentTypeId,
        State = InvoiceState.Draft,
        Payment = InvoicePaymentState.Unpaid,
        CreatedBy = author,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Положить значения колонок и остаток схемы разом — так, как их разобрал
    /// <c>InvoiceRequisites</c>.
    ///
    /// <para>Одним методом, а не сеттером на каждую колонку: правка счёта приходит целиком, и
    /// «метка снимается с того поля, которое изменилось» считается сравнением ДО и ПОСЛЕ — то есть
    /// требует видеть все значения сразу.</para>
    /// </summary>
    /// <param name="dueDateByHand">
    /// Правит ли срок ЧЕЛОВЕК. Признак «срок задан вручную» помнится навсегда: правило подстановки
    /// (<c>C4</c>) обязано обходить такой срок стороной, а из самого значения это не выводится никак.
    ///
    /// <para>⚠️ Параметром, а не «значение отличается от лежащего». Отличается оно и при СОЗДАНИИ —
    /// лежащего там нет, и любой присланный срок не равен пустому. Выведи мы признак из разницы, счёт,
    /// заведённый распознаванием, оказался бы помечен ручным навсегда: «оплатить до» стоит в бумаге
    /// поставщика, то есть правило подстановки обходило бы стороной ровно те счета, ради которых
    /// заводится, а сбросить признак нечем.</para>
    /// </param>
    public void Apply(InvoiceColumns columns, JsonDocument data, bool dueDateByHand)
    {
        Number = columns.Number;
        IssuedOn = columns.IssuedOn;
        SupplierId = columns.SupplierId;
        PayerId = columns.PayerId;
        Purpose = columns.Purpose;
        Total = columns.Total;
        VatTotal = columns.VatTotal;
        ShippedOn = columns.ShippedOn;
        DeferralDays = columns.DeferralDays;
        Data = data;

        if (dueDateByHand && columns.DueDate != DueDate) DueDateManual = true;
        DueDate = columns.DueDate;

        Touch();
    }

    /// <summary>
    /// «Когда правили» — одной точкой, как <c>TouchUpdatedAt</c> у сущностей ядра. Время берётся
    /// здесь, а не приходит параметром: в этом решении его так берут все, и часы, переданные снаружи
    /// у одной сущности из сотни, читались бы как намёк на то, чего нет.
    /// </summary>
    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// «Разобран» (ТЗ COST-9, задачи C2 и F1): строки есть, у всех позиция номенклатуры, обязательные
    /// поля заполнены, разноска по стройкам сходится. Проверяет это адрес перехода — он видит строки,
    /// схему и разноску; здесь только запись оси.
    /// </summary>
    public void MarkParsed()
    {
        State = InvoiceState.Parsed;
        Touch();
    }

    /// <summary>
    /// Вернуть в черновик.
    ///
    /// <para>Нужен по двум разным поводам, и оба настоящие. Первый: человек разобрал счёт и увидел
    /// ошибку — дверь, открывающаяся в одну сторону, оставила бы его с неверно разобранным счётом и
    /// ничем. Второй: правка строк сняла позицию или убрала строки — тогда «разобран» стал бы
    /// утверждением, которое перестало быть правдой, и держать его значило бы врать отбору «Разобрать».
    /// Второй случай делает это САМ, но говорит вслух — записью в журнал и состоянием в ответе.</para>
    /// </summary>
    public void ReturnToDraft()
    {
        State = InvoiceState.Draft;
        Touch();
    }

    /// <summary>
    /// Изменилось содержимое счёта, которое лежит НЕ в его колонках, — строки.
    ///
    /// <para>Строки — часть счёта, а не соседняя сущность: человек, открывший реестр, спрашивает «когда
    /// счёт правили», а не «когда правили его шапку». До этого метода правка строк время счёта не
    /// трогала, и <c>updatedAt</c> в ответе оставался прежним при другом содержимом (issue #1171).</para>
    /// </summary>
    public void ContentChanged() => Touch();

    /// <summary>Скан счёта: пришёл файлом или сканом (ТЗ COST-5).</summary>
    public void AttachScan(string blobPath, string fileName, string mimeType, long size)
    {
        ScanBlobPath = blobPath;
        ScanFileName = fileName;
        ScanMimeType = mimeType;
        ScanSize = size;
        Touch();
    }

    /// <summary>
    /// Пометить поля как «распознано, не подтверждено». Кладёт распознавание (B1b, issue #1077);
    /// в C1 это делает создание записи, которому метки передали.
    /// </summary>
    public void MarkUnconfirmed(IEnumerable<string> keys)
    {
        unconfirmed = [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Touch();
    }

    /// <summary>
    /// Снять метку с названных полей — правкой поля или действием «Всё верно» по блоку формы.
    ///
    /// <para>Блок называет КЛИЕНТ, перечисляя ключи: блоки — это видимые группы формы, и сервер о
    /// них не знает. Придумай он их сам — «Всё верно» подтверждало бы поля, которых человек на
    /// экране не видел.</para>
    /// </summary>
    /// <returns>Сколько меток снялось — для отчёта и для журнала действий.</returns>
    public int Confirm(IEnumerable<string> keys)
    {
        var drop = keys.ToHashSet(StringComparer.Ordinal);
        var before = unconfirmed.Count;
        unconfirmed = [.. unconfirmed.Where(k => !drop.Contains(k))];
        if (unconfirmed.Count != before) Touch();
        return before - unconfirmed.Count;
    }
}

/// <summary>
/// Значения колонок счёта одним значением — ровно те поля схемы, за которыми стоит колонка
/// (ТЗ CORE-15).
///
/// <para>Нужен затем, что разбор реквизитов и запись в сущность — два разных дела: первое умеет
/// отказать по виду значения, второе обязано положить уже разобранное. Без него отказ разбора
/// случался бы на половине записанных полей.</para>
/// </summary>
public sealed record InvoiceColumns(
    string? Number,
    DateOnly? IssuedOn,
    Guid? SupplierId,
    Guid? PayerId,
    string? Purpose,
    decimal? Total,
    decimal? VatTotal,
    DateOnly? ShippedOn,
    int? DeferralDays,
    DateOnly? DueDate);
