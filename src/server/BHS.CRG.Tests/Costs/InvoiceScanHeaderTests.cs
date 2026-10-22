using System.Text.Json.Nodes;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Шапка скана по правилу «ключ поля профиля = ключ поля счёта» (issue #1483). Числа выдуманные.
/// Сопоставление ключей — ядра (<c>RecognitionTargetMatcherTests</c>); здесь — что модуль делает с целями.
/// </summary>
public class InvoiceScanHeaderTests
{
    private static readonly ModuleRecognitionTargets Targets = CostsRecognitionProfiles.InvoiceTargets;

    private static ModuleFieldTarget Module(string key, string type = "string") =>
        new(key, ModuleTargetOrigin.Module, type, Title: InvoiceRequisites.TitleOf(key));

    /// <summary>Сторож: таблица разбора и есть объявление — «объявили, но не раскладываем» невозможно.</summary>
    [Fact]
    public void Разбор_есть_у_каждой_цели_поля_и_только_у_них()
    {
        var declared = Targets.Fields.Where(t => t.Use == ModuleTargetUse.Field).Select(t => t.Key).Order();
        Assert.Equal(declared, InvoiceScanHeader.Parsers.Keys.Order());
    }

    /// <summary>
    /// Сторож: каждое поле типа счёта решено — поле, вход или недоступное с причиной. Новое поле модуля без
    /// решения иначе ушло бы в «поле заказчика» и заполнялось бы общим разбором, мимо своего.
    /// </summary>
    [Fact]
    public void Каждое_поле_типа_счёта_решено()
    {
        var declared = Targets.Fields.Select(t => t.Key).ToHashSet();
        Assert.All(CostsRecordTypes.Invoice.Fields, field => Assert.Contains(field.Key, declared));
        Assert.Equal(CostsRecordTypes.InvoiceCode, Targets.RecordType);
    }

    /// <summary>Сторож: у заводского профиля нет мёртвых полей — каждое ведёт в цель или прежним ключом.</summary>
    [Fact]
    public void Каждое_поле_заводского_профиля_куда_то_ложится()
    {
        var known = Targets.Fields.SelectMany(t => t.LegacyKeys.Append(t.Key)).ToHashSet();
        Assert.All(CostsRecognitionProfiles.InvoiceHeader, field => Assert.Contains(field.Name, known));
    }

    /// <summary>
    /// Снимок прежних ключей: это совместимость с заводским профилем, а не механизм синонимов. Расширение
    /// списка — правка снимка, то есть осознанное решение.
    /// </summary>
    [Fact]
    public void Прежние_ключи_закрытый_список()
    {
        var legacy = Targets.Fields.SelectMany(t => t.LegacyKeys.Select(l => $"{l}→{t.Key}")).Order();
        Assert.Equal(["ДатаСчёта→Дата", "НомерСчёта→Номер", "СуммаКОплате→Итого"], legacy);
    }

    [Fact]
    public void Основание_ложится_в_Основание()
    {
        var plan = InvoiceScanHeader.Of(
            new Dictionary<string, string?> { ["Основание"] = "Договор № 5" },
            new Dictionary<string, ModuleFieldTarget?> { ["Основание"] = Module(InvoiceRequisites.BasisKey) });
        var only = Assert.Single(plan.Candidates);
        Assert.Equal(InvoiceRequisites.BasisKey, only.Key);
        Assert.Equal("Договор № 5", only.Value!.GetValue<string>());
    }

    /// <summary>Точный ключ старше прежнего; второе прочитанное не теряется молча — названо.</summary>
    [Fact]
    public void Точный_ключ_старше_прежнего()
    {
        var plan = InvoiceScanHeader.Of(
            new Dictionary<string, string?> { ["НомерСчёта"] = "СЧ-1", ["Номер"] = "СЧ-2" },
            new Dictionary<string, ModuleFieldTarget?>
            {
                ["НомерСчёта"] = Module(InvoiceRequisites.NumberKey),
                ["Номер"] = Module(InvoiceRequisites.NumberKey),
            });

        var only = Assert.Single(plan.Candidates);
        Assert.Equal("СЧ-2", only.Value!.GetValue<string>());
        Assert.Equal(
            InvoiceScanUnplaced.InHeader("НомерСчёта", "СЧ-1", InvoiceScanUnplaced.DuplicateKey, "Номер",
                InvoiceRequisites.TitleOf(InvoiceRequisites.NumberKey)),
            Assert.Single(plan.Unplaced));
    }

    [Fact]
    public void Поле_заказчика_разбирается_по_виду()
    {
        var plan = InvoiceScanHeader.Of(
            new Dictionary<string, string?> { ["Объём"] = "12,5", ["Подписан"] = "01.10.2026", ["Код"] = "Д-7" },
            new Dictionary<string, ModuleFieldTarget?>
            {
                ["Объём"] = new("Объём", ModuleTargetOrigin.Type, "number"),
                ["Подписан"] = new("Подписан", ModuleTargetOrigin.Type, "date"),
                ["Код"] = new("Код", ModuleTargetOrigin.Type, "string"),
            });

        var values = plan.Candidates.ToDictionary(c => c.Key, c => c.Value!);
        Assert.Equal(12.5m, values["Объём"].GetValue<decimal>());
        Assert.Equal("2026-10-01", values["Подписан"].GetValue<string>());
        Assert.Equal("Д-7", values["Код"].GetValue<string>());
    }

    /// <summary>«1.250» — тысяча двести пятьдесят или одна целая двадцать пять? Не угадываем: в предложение.</summary>
    [Theory]
    [InlineData("1.250")]
    [InlineData("1,250")]
    [InlineData("двенадцать")]
    public void Неоднозначное_число_не_разбирается(string text) =>
        Assert.Null(InvoiceScanHeader.ByType("number", text));

    [Theory]
    [InlineData("12,5", "12.5")]
    [InlineData("1 234,567 8", "1234.5678")]
    [InlineData("0,125", "0.125")]
    [InlineData("1.234,5", "1234.5")]
    // Тысячи отделены пробелом — дробь однозначна (ревью PR #1523).
    [InlineData("1 234,567", "1234.567")]
    [InlineData("1 234 567,891", "1234567.891")]
    [InlineData("7", "7")]
    public void Число_поля_заказчика(string text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            RecognizedValues.Decimal(text));

    /// <summary>
    /// О не легшем сказано записями (issue #1484): поля нет — «no-field» с ключом как прочитан; поле не
    /// заполняется — с причиной поля; служебный вход и пустое — молча.
    /// </summary>
    [Fact]
    public void О_не_легшем_сказано_с_причиной()
    {
        var plan = InvoiceScanHeader.Of(
            new Dictionary<string, string?>
            {
                ["Опечатка "] = "что-то",
                ["Срок"] = "01.11.2026",
                ["ИННПоставщика"] = "7701234567",
                ["Пусто"] = null,
            },
            new Dictionary<string, ModuleFieldTarget?>
            {
                ["Опечатка "] = null,
                ["Срок"] = new("Срок", ModuleTargetOrigin.Module, "date", "срок считает правило", "Оплатить до"),
                ["ИННПоставщика"] = new("ИННПоставщика", ModuleTargetOrigin.Input, "string"),
                ["Пусто"] = null,
            });

        Assert.Empty(plan.Candidates);
        Assert.Equal(
        [
            new UnplacedRead(InvoiceScanUnplaced.Header, null, "Опечатка ", "что-то", InvoiceScanUnplaced.NoField),
            // Поле названо подписью (ревью PR #1576): по ключу «Срок» не узнать, что задето «Оплатить до».
            new UnplacedRead(InvoiceScanUnplaced.Header, null, "Срок", "01.11.2026", InvoiceScanUnplaced.NotRecognizable,
                "срок считает правило", "Оплатить до"),
        ], plan.Unplaced);
    }

    /// <summary>
    /// Ревью PR #1523: предложения, записанные до #1483 строкой, отдаются с разобранным значением — тем же
    /// разбором, — иначе у них пропало бы «Взять». Не разобранное — только текст; сторона — как лежит.
    /// </summary>
    [Fact]
    public void Прежние_предложения_строкой_разбираются_при_показе()
    {
        using var stored = System.Text.Json.JsonDocument.Parse(
            """{"Номер":"СЧ-1","Итого":"1.234","Назначение":"Договор № 5","Поставщик":{"text":"ООО","entryId":"b"}}""");
        var shown = InvoiceScanHeader.Shown(stored.RootElement)!.Value;

        Assert.Equal("СЧ-1", shown.GetProperty("Номер").GetProperty("value").GetString());
        Assert.Equal("1.234", shown.GetProperty("Итого").GetProperty("text").GetString());
        Assert.False(shown.GetProperty("Итого").TryGetProperty("value", out _));
        Assert.Equal("Договор № 5", shown.GetProperty("Назначение").GetProperty("value").GetString());
        Assert.Equal("b", shown.GetProperty("Поставщик").GetProperty("entryId").GetString());
    }

    [Fact]
    public void Предложение_несёт_значение_а_не_разобранное_только_текст()
    {
        Assert.Equal("""{"text":"1 234,50","value":1234.5}""", InvoiceScanHeader.Offer("1 234,50", JsonValue.Create(1234.5m)).ToJsonString());
        Assert.Equal("""{"text":"1.234"}""", InvoiceScanHeader.Offer("1.234", null).ToJsonString());
    }
}
