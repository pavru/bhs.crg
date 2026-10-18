using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Запрос к модели по счёту — побайтово (issue #1481). Описания колонок строк уходят модели: это
/// единственный канал смысла колонки, и уточнение профиля обязано влиять на распознавание строк.
///
/// <para><b>Сторож задачи — снимок ниже.</b> Запрос меняет качество распознавания, поэтому каждая его
/// правка — осознанная: тест переписывают вместе с ней, а не узнают о ней от заказчика. Последний раз
/// переписан в #1481 (описания колонок строк и их пересмотр).</para>
/// </summary>
public class InvoicePromptTests
{
    private const string Factory = """
        Ты извлекаешь реквизиты из счёта на оплату (может быть на нескольких страницах —
        это ОДИН документ, не путай разные страницы с разными счетами).
        Извлеки значения СТРОГО для перечисленных полей. Ответ — один JSON-объект {"путь": "значение"} без markdown и пояснений.
        Даты возвращай в ISO (ГГГГ-ММ-ДД). Если значения нет — пустая строка. Не выдумывай.

        Поля (путь — название — тип):
        - НомерСчёта — Номер счёта — string
        - ДатаСчёта — Дата счёта — string
        - Поставщик — Поставщик (исполнитель) — string
        - ИННПоставщика — ИНН поставщика — string
        - Плательщик — Плательщик (заказчик) — string
        - ИННПлательщика — ИНН плательщика — string
        - Основание — Основание (договор/назначение платежа) — string
        - СуммаКОплате — Сумма к оплате (итого) — string
        - ВТомЧислеНДС — В том числе НДС — string
        - Товары — Таблица товаров/услуг — JSON-массив объектов с полями Наименование/ЕдиницаИзмерения/Количество/Цена/Сумма/СтавкаНДС/СуммаНДС — json-array

        Поле Товары — верни ЗНАЧЕНИЕМ этого ключа настоящий JSON-массив объектов
        (не строку), по одному объекту на строку таблицы товаров/услуг. Если товаров нет — пустой массив [].
        Ключи объекта строки (ключ — что в нём):
        - Наименование — Наименование товара/услуги, как в строке счёта, без номера строки
        - ЕдиницаИзмерения — Единица измерения, как в счёте (шт, м, кг, усл.)
        - Количество — Количество из графы количества; не склей его с ценой: количество × цена даёт сумму строки (без НДС или с ним)
        - Цена — Цена за единицу, как напечатана в счёте; если цен две, без НДС и с НДС, бери цену с НДС; не пересчитывай
        - Сумма — Сумма по строке (итог строки); если сумм две, без НДС и с НДС, бери сумму с НДС
        - СтавкаНДС — Ставка НДС по строке (например 22 %, 10 %, «Без НДС»)
        - СуммаНДС — Сумма НДС по строке из графы суммы НДС; нет такой графы — пусто

        """;

    [Fact]
    public void Заводской_запрос_счёта_побайтово() =>
        Assert.Equal(Factory.ReplaceLineEndings("\n"),
            RecognitionShared.BuildInvoicePrompt(TestRecognition.InvoiceCall).ReplaceLineEndings("\n"));

    /// <summary>
    /// Колонка, добавленная администратором, уходит модели со своим описанием; колонка без описания —
    /// одним ключом, без повтора «Ключ — Ключ» и без висящего тире. Профиль идёт через рабочее
    /// отображение (<see cref="ResolvedRecognitionProfile" />): «пустое описание → ключ» решает оно, и
    /// тест, подставивший описание руками, его бы не заметил.
    /// </summary>
    [Fact]
    public void Своя_колонка_уходит_модели_с_описанием()
    {
        var prompt = Prompt(
            new RecognitionProfileField("Артикул", "Код товара поставщика из отдельной колонки; не номер строки"),
            new RecognitionProfileField("Страна"));

        Assert.Contains("\n- Артикул — Код товара поставщика из отдельной колонки; не номер строки\n", prompt);
        Assert.Contains("\n- Страна\n", prompt);
        Assert.DoesNotContain("\n- Страна —", prompt);
    }

    /// <summary>
    /// Тип и перечень, выбранные в редакторе, доходят до модели; строка — умолчание и не печатается.
    /// </summary>
    [Fact]
    public void Тип_и_варианты_колонки_уходят_модели()
    {
        var prompt = Prompt(
            new RecognitionProfileField("ДатаПоставки", "Дата поставки", "date"),
            new RecognitionProfileField("Склад", "Склад получателя", Options: ["Основной", "Резервный"]));

        Assert.Contains("\n- ДатаПоставки — Дата поставки (тип date)\n", prompt);
        Assert.Contains("\n- Склад — Склад получателя (варианты: Основной, Резервный)\n", prompt);
    }

    /// <summary>
    /// Описание в несколько строк печатается одной: продолжение, начатое с «-», иначе стало бы в списке
    /// ключей новым ключом.
    /// </summary>
    [Fact]
    public void Описание_в_несколько_строк_не_рвёт_список_ключей()
    {
        var prompt = Prompt(new RecognitionProfileField("Артикул", "Код поставщика\r\n- не номер строки"));

        Assert.Contains("\n- Артикул — Код поставщика - не номер строки\n", prompt);
        Assert.DoesNotContain("\n- не номер строки", prompt);
    }

    /// <summary>
    /// Поле шапки с ключом «Товары» стоит в списке раньше поля-массива и прежде гасило весь список
    /// колонок — без отказа.
    /// </summary>
    [Fact]
    public void Поле_шапки_с_ключом_таблицы_не_гасит_колонки()
    {
        RecognitionField[] header = [.. TestRecognition.InvoiceHeader, new(InvoiceFields.LineItemsPath, "Товары", "string")];
        var prompt = RecognitionShared.BuildInvoicePrompt(
            RecognitionKinds.ComposeCallFields(RecognitionProfileKind.Invoice, header, TestRecognition.InvoiceLines));

        Assert.Contains("Ключи объекта строки", prompt);
    }

    /// <summary>Запрос по заводскому профилю счёта с добавленными колонками — через рабочее отображение.</summary>
    private static string Prompt(params RecognitionProfileField[] extra)
    {
        static RecognitionProfileField Field(BHS.CRG.Modules.Recognition.ModuleRecognitionField f) =>
            new(f.Name, f.Description, f.Type, f.Options);

        var profile = new ResolvedRecognitionProfile(Guid.NewGuid(), "Счёт", RecognitionProfileKind.Invoice,
            [.. CostsRecognitionProfiles.InvoiceHeader.Select(Field)],
            [.. CostsRecognitionProfiles.InvoiceLines.Select(Field), .. extra],
            null);
        return RecognitionShared.BuildInvoicePrompt(RecognitionKinds.ComposeCallFields(profile)).ReplaceLineEndings("\n");
    }

    /// <summary>
    /// У таблиц документа колонки и так печатаются полями с описаниями — второго списка у них нет:
    /// их запросы этой правкой не тронуты.
    /// </summary>
    [Fact]
    public void Запрос_таблицы_документа_не_получает_второго_списка_колонок()
    {
        var prompt = RecognitionShared.BuildTablePrompt(
            RecognitionKinds.ComposeCallFields(RecognitionProfileKind.Table, [], TestRecognition.Specification));

        Assert.DoesNotContain("Ключи объекта строки", prompt);
    }
}
