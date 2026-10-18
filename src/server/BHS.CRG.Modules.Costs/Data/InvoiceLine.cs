namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Строка счёта (задача C2 этапа 2, issue #1078, ТЗ COST-7, COST-7.1, COST-7.2).
///
/// <para><b>Своей таблицей, а не полем-массивом в схеме счёта.</b> На строку опирается КОД: отбор
/// «ждёт номенклатуры», сверка суммы строк с суммой к оплате, разноска по количеству (F1) и связь
/// материала с документом качества. Объяви мы строки массивом в схеме, каждая из этих опор читала бы
/// JSON — и первая же правка схемы администратором меняла бы поведение кода молча (правило ТЗ
/// CORE-15, записано в <c>CostsRecordTypes</c> ещё до того, как строки появились).</para>
///
/// <para><b>Схемы у строки нет вовсе</b>, и дописать в неё поле заказчик не может — в отличие от
/// самого счёта (уровень «расширяемый»). Причина не в экономии: строка целиком состоит из того, на
/// что опирается код, и «остальное по схеме» здесь было бы местом, куда складывают то, что потом
/// никто не считает. Понадобится колонка — она приедет колонкой.</para>
///
/// <para>⚠️ <b>Ссылка на номенклатуру — идентификатором, без внешнего ключа</b>, как и поставщик у
/// счёта: позиция живёт в ядре (<c>domain_objects</c>), схема модуля от схемы ядра ключами не
/// связывается. Целость проверяется чтением через <c>IModuleCatalog</c>, и «ссылка есть, записи нет»
/// показывается человеку, а не прячется.</para>
/// </summary>
public sealed class InvoiceLine
{
    /// <summary>Пределы длины — те же числа, что у колонок базы. Почему одним местом — см.
    /// <see cref="Invoice.NumberLength"/>.</summary>
    public const int SupplierCodeLength = 100;

    /// <inheritdoc cref="SupplierCodeLength"/>
    public const int UnitLength = 50;

    /// <summary>Для EF.</summary>
    private InvoiceLine() { }

    public Guid Id { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>
    /// Порядок строки в бумаге поставщика. Хранится, а не выводится из времени создания: строки
    /// приезжают вставкой из буфера целой таблицей, и порядок в ней — часть данных. Сверять наш
    /// список с бумагой человек будет строка за строкой.
    /// </summary>
    public int Ordinal { get; private set; }

    /// <summary>
    /// Позиция номенклатуры ЯДРА — обязательная по ТЗ (COST-7) ссылка… которой у живой строки может
    /// не быть, и это штатное состояние, а не нарушение.
    ///
    /// <para>Разница в том, КОГДА обязательность проверяется. Счёт приезжает сканом, наименования в
    /// нём — слова поставщика («кабель ВВГнг-LS 3х2,5»), и сопоставить их с справочником может только
    /// человек или таблица соответствий (C3). Поэтому строка без позиции живёт, счёт с такими строками
    /// живёт и попадает в отбор «Разобрать» — а вот <b>разобранным он не становится</b>: переход
    /// «черновик → разобран» отказывает, перечисляя строки без позиции.</para>
    ///
    /// <para>⚠️ Чего здесь нет и быть не может: <b>наименования вместо ссылки</b>. Свободный текст
    /// допускается в примечании и в <see cref="SupplierText" /> — цитате из бумаги, — но позицию учёта
    /// он не заменяет никогда: без ссылки на справочник нельзя ни свести затраты, ни связать материал с
    /// документом качества, ни сказать монтажнику, что ему отпустили.</para>
    /// </summary>
    public Guid? NomenclatureId { get; private set; }

    /// <summary>
    /// Наименование позиции ТАК, КАК ОНО СТОИТ В БУМАГЕ поставщика.
    ///
    /// <para>Хранится по двум причинам, и обе — не про удобство. Первая: строка, ждущая
    /// сопоставления, иначе не отличима от пустой — в списке «Разобрать» человек видел бы количество и
    /// цену без всякого намёка на то, что ему сопоставлять. Вторая: это ключ таблицы соответствий
    /// «поставщик + артикул или наименование → позиция» (ТЗ COST-7.1, задача C3) — соответствие
    /// запоминается ровно по тому тексту, который приехал.</para>
    ///
    /// <para>⚠️ Подменять им ссылку нельзя: см. <see cref="NomenclatureId" />. Заполненный текст без
    /// ссылки означает «эту строку ещё не разобрали», а не «позиция называется так».</para>
    /// </summary>
    public string? SupplierText { get; private set; }

    /// <summary>Артикул поставщика из бумаги — вторая половина ключа соответствий (ТЗ COST-7.1).</summary>
    public string? SupplierCode { get; private set; }

    /// <summary>
    /// Единица измерения — ИЗ БУМАГИ, а не из номенклатуры.
    ///
    /// <para>⚠️ Подстановки из выбранной позиции здесь НЕТ нарочно. Поставщик продаёт барабанами и
    /// бухтами, номенклатура учитывается в метрах, и подставленная единица разошлась бы с бумагой
    /// молча — то есть количество из счёта оказалось бы записано не в той мере. Пересчёт единиц — это
    /// отдельная задача с коэффициентами, а не строка кода здесь.</para>
    /// </summary>
    public string? Unit { get; private set; }

    public decimal? Quantity { get; private set; }

    /// <summary>
    /// Цена за единицу — КАК В БУМАГЕ. Её смысл зависит от режима счёта, и она единственное число строки,
    /// у которого так (issue #1497): при «в том числе» цена с НДС, при НДС сверху — без НДС. Пересчитать
    /// её к одному виду нельзя: цена с НДС от цены без НДС даёт доли копейки, а колонка — до копеек.
    ///
    /// <para>⚠️ Для расчёта её не читает никто, кроме <see cref="InvoiceLineValues.Completed" />
    /// (количество × цена) — только показ, снимок версии и ответ. Понадобится читать её как деньги
    /// («последняя цена поставщика») — только вместе с режимом; кто начнёт, того назовёт перепись
    /// <c>VatModeInventoryTests</c>.</para>
    /// </summary>
    public decimal? Price { get; private set; }

    /// <summary>
    /// Ставка НДС в процентах — любая от 0 до 100: 22, 10, 0 (основная с 2026 года — 22 %, список
    /// нарочно не зашит). <c>null</c> — в бумаге не указана, а не «без НДС»: «без НДС» — это 0.
    /// </summary>
    public decimal? VatRate { get; private set; }

    /// <summary>
    /// Сумма НДС строки. Присланная — как есть; не присланную считает <see cref="InvoiceLineValues" />
    /// по ставке и режиму счёта: «в том числе» — от суммы с НДС, сверху — от суммы без НДС.
    /// </summary>
    public decimal? VatAmount { get; private set; }

    /// <summary>
    /// Сумма строки <b>С НДС — всегда, в обоих режимах счёта</b> (issue #1497). Её читают разноска,
    /// книга, оплата, отчёты и реестр, и ни один из них режима не знает.
    ///
    /// <para>«В том числе»: присланная — как есть, иначе количество × цена: бумага иногда округляет
    /// иначе, чем мы, и её число важнее нашего. Сверху: <see cref="NetAmount" /> + <see cref="VatAmount" />
    /// — производное, и <b>пустое, когда НДС неизвестен</b> (нет ни ставки, ни суммы НДС): бумажная сумма
    /// без НДС, выданная за сумму с НДС, ошиблась бы на весь налог молча. Держит это и база —
    /// <c>ck_invoice_lines_net_amount</c>.</para>
    /// </summary>
    public decimal? Amount { get; private set; }

    /// <summary>
    /// Сумма строки ИЗ БУМАГИ, без НДС — только у счёта с НДС сверху; при «в том числе» пусто
    /// (issue #1497). Бумажное число лежит здесь ровно как прислано — «присланное не трогается никогда»
    /// не нарушается, сумма с НДС рядом названа производной.
    ///
    /// <para>При «в том числе» она не хранится нарочно: там сумма без НДС — производное с округлением, и
    /// второе число могло бы разойтись с первым.</para>
    /// </summary>
    public decimal? NetAmount { get; private set; }

    /// <summary>Примечание — единственное место, где свободный текст ничего не подменяет (ТЗ COST-7).</summary>
    public string? Note { get; private set; }

    /// <summary>
    /// Соответствие, из которого позиция ПОДСТАВЛЕНА (задача C3, issue #1079, ТЗ COST-7.1), — пометка
    /// «(запомнено)». <c>null</c> — позицию выбрал человек либо её нет.
    ///
    /// <para><b>Идентификатором, а не флагом.</b> По нему на чтении видно, что соответствие с тех пор
    /// направили на другую позицию или забыли вовсе — и строка говорит об этом, а не продолжает
    /// выглядеть подтверждённой. Флаг «подставлено» этого не знал бы.</para>
    ///
    /// <para>Внешнего ключа нет нарочно: забытое соответствие не должно ни держаться строками, ни
    /// молча снимать с них пометку — «подставлено из того, чего больше нет» и есть то, что человеку
    /// надо увидеть.</para>
    /// </summary>
    public Guid? MatchedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static InvoiceLine Create(Guid invoiceId) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Значения строки, как они лежат сейчас. Нужны затем, чтобы отличить правку от повторной отправки
    /// того же набора: форма присылает строки целиком на каждое сохранение, и запись в журнал без этой
    /// сверки означала бы «строки изменены» там, где не изменилось ничего.
    /// </summary>
    public CompletedLine Snapshot() => new(new(
        NomenclatureId, SupplierText, SupplierCode, Unit, Quantity, Price, VatRate, VatAmount, Amount,
        Note, MatchedBy, NetAmount));

    /// <summary>
    /// Положить значения строки разом. Одним методом, как у счёта: строки приходят таблицей целиком —
    /// и с клавиатуры, и вставкой из буфера, — а сеттер на колонку приглашал бы править их по одной,
    /// чего форма не делает.
    ///
    /// <para>⚠️ Принимает только <see cref="CompletedLine" /> (issue #1497): недосчитанное сюда не
    /// передать, и забытый режим — ошибка компиляции, а не теста. Цена забывчивости здесь — НДС.</para>
    /// </summary>
    public void Apply(int ordinal, CompletedLine line)
    {
        var values = line.Values;
        Ordinal = ordinal;
        NomenclatureId = values.NomenclatureId;
        SupplierText = values.SupplierText;
        SupplierCode = values.SupplierCode;
        Unit = values.Unit;
        Quantity = values.Quantity;
        Price = values.Price;
        VatRate = values.VatRate;
        VatAmount = values.VatAmount;
        Amount = values.Amount;
        NetAmount = values.NetAmount;
        Note = values.Note;
        MatchedBy = values.MatchedBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Строка, досчитанная по режиму счёта, — единственное, что принимает запись (issue #1497,
/// <see cref="InvoiceLine.Apply" />). Создают её двое: <see cref="InvoiceLineValues.Completed" /> и
/// снимок лежащей строки — она досчитана при записи.
/// </summary>
/// <param name="Values">Значения с суммой С НДС в <see cref="InvoiceLineValues.Amount" /> и бумажной
/// суммой без НДС в <see cref="InvoiceLineValues.NetAmount" /> — у счёта с НДС сверху.</param>
public sealed record CompletedLine
{
    internal CompletedLine(InvoiceLineValues values) => Values = values;

    public InvoiceLineValues Values { get; }

    /// <summary>
    /// Строка скана — досчитанная самим распознаванием, ВЫБОРОЧНО (<c>InvoiceScanLines</c>): графа
    /// стояла в бумаге и не прочиталась — её сумма остаётся пустой, а не посчитанной. Общий
    /// <see cref="InvoiceLineValues.Completed" /> досчитал бы её, и посчитанное выглядело бы прочитанным.
    ///
    /// <para>⚠️ Только «в том числе»: строки скана ложатся только в такой счёт, пока режим со скана не
    /// умеет распознавание (issue #1501).</para>
    /// </summary>
    internal static CompletedLine Scanned(InvoiceLineValues included) => included.NetAmount is null
        ? new(included)
        : throw new ArgumentException("Строка скана с суммой без НДС: распознавание читает строки только «в том числе».",
            nameof(included));
}

/// <summary>
/// Значения строки — разобранные, как в бумаге. В колонки они ложатся досчитанными по режиму счёта —
/// через <see cref="Completed" /> и <see cref="CompletedLine" />.
///
/// <para>Отдельно от сущности по той же причине, что <c>InvoiceColumns</c> у счёта: разбор присланного
/// умеет отказать, а запись обязана положить уже разобранное. Здесь же живёт арифметика — суммы,
/// которых в бумаге не оказалось.</para>
/// </summary>
public sealed record InvoiceLineValues(
    Guid? NomenclatureId,
    string? SupplierText,
    string? SupplierCode,
    string? Unit,
    decimal? Quantity,
    decimal? Price,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? Amount,
    string? Note,
    Guid? MatchedBy = null,
    decimal? NetAmount = null)
{
    /// <summary>
    /// Досчитать то, чего в бумаге нет: сумму строки и сумму НДС, — по режиму счёта (issue #1497).
    ///
    /// <para><b>Присланное не трогается никогда.</b> Бумага поставщика округляет по-своему, и наше
    /// «более правильное» число разошлось бы с тем, что человек видит на скане рядом. Считаем только
    /// то, чего нет вовсе.</para>
    ///
    /// <para><b>«В том числе»</b> (<see cref="VatMode.Included" />): бумажная сумма — в
    /// <see cref="Amount" />, иначе количество × цена; НДС — <c>сумма × ставка ÷ (100 + ставка)</c>.</para>
    ///
    /// <para><b>Сверху</b> (<see cref="VatMode.OnTop" />): бумажная сумма — в <see cref="NetAmount" />,
    /// иначе количество × цена; НДС — <c>сумма × ставка ÷ 100</c>, построчно, копейки «от нуля»; сумма с
    /// НДС — их сложение. Разница с НДС бумаги, посчитанным от итога, — копейки, и они уходят в допуск
    /// расхождения, как любое округление. НДС неизвестен — сумма с НДС пустая, а не равная бумажной.</para>
    ///
    /// <para>⚠️ <b>Режим обязателен, умолчания нет нарочно</b> — тот же приём, что у допуска в
    /// <c>AllocationMath.Of</c>: с умолчанием забытое место считало бы «в том числе» у счёта сверху.</para>
    /// </summary>
    /// <exception cref="ArgumentException">Бумажная сумма лежит не в своём поле: <see cref="Amount" />
    /// при НДС сверху либо <see cref="NetAmount" /> при «в том числе». Человеку об этом отказывает разбор
    /// запроса раньше (<c>InvoiceLineRequests</c>) — здесь последняя дверь против удвоения НДС.</exception>
    public CompletedLine Completed(VatMode mode)
    {
        var paper = mode switch
        {
            VatMode.Included when NetAmount is null => Amount,
            VatMode.OnTop when Amount is null => NetAmount,
            _ => throw new ArgumentException(
                $"Бумажная сумма строки при режиме {mode} лежит не в своём поле — сумма с НДС посчиталась бы дважды.",
                nameof(mode)),
        };
        var sum = paper ?? (Quantity is { } quantity && Price is { } price ? Money(quantity * price) : null);

        if (mode == VatMode.Included)
        {
            var included = VatAmount ?? (sum is { } total && VatRate is { } rate && rate >= 0
                ? Money(total * rate / (100 + rate))
                : null);
            return new(this with { Amount = sum, VatAmount = included });
        }

        var vat = VatAmount ?? (sum is { } net && VatRate is { } onTop && onTop >= 0 ? Money(net * onTop / 100) : null);
        return new(this with { NetAmount = sum, VatAmount = vat, Amount = sum + vat });
    }

    /// <summary>
    /// Бумажную сумму строки — в поле её режима: сумма строки из бумаги при НДС сверху лежит в
    /// <see cref="NetAmount" />. Для тех, кто читает бумагу одним числом «сумма» и режим узнаёт позже, —
    /// распознавания (issue #1497).
    /// </summary>
    public InvoiceLineValues PaperIn(VatMode mode) =>
        mode == VatMode.OnTop && NetAmount is null ? this with { NetAmount = Amount, Amount = null } : this;

    /// <summary>
    /// Копейки — с округлением «от нуля», как считают деньги люди и как считает бумага. Банковское
    /// округление (по умолчанию у .NET) даёт на половинке другой ответ, и расхождение в копейку
    /// объяснить человеку нечем.
    /// </summary>
    internal static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
