using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Реквизиты «как ляжут» (<see cref="InvoiceRequisites.Split" />) против реквизитов «как лежат»
/// (<see cref="InvoiceRequisites.Merge" />). Охрана записи ядра и сравнение «что изменилось» видят первое,
/// форма после сохранения — второе; разойдись они, и сумма «1 234,50» либо не сохраняется вовсе (охрана:
/// «ожидается число, а хранится строка»), либо каждое сохранение считается правкой и снимает метки
/// распознавания.
/// </summary>
public class InvoiceRequisitesTests
{
    private static readonly string Supplier = Guid.NewGuid().ToString();
    private static readonly string Payer = Guid.NewGuid().ToString();

    /// <summary>
    /// Каждая колонка, которую присылает человек, — в том виде, в каком её набирают и вставляют: суммы с
    /// пробелом и запятой, отсрочка строкой, ссылка с лишним полем подписи.
    /// </summary>
    private static readonly Dictionary<string, JsonNode> Typed = new()
    {
        [InvoiceRequisites.NumberKey] = "СЧ-1",
        [InvoiceRequisites.DateKey] = "2026-10-01",
        [InvoiceRequisites.SupplierKey] = new JsonObject { ["$ref"] = "catalog", ["entryId"] = Supplier, ["title"] = "ООО «Поставщик»" },
        [InvoiceRequisites.PayerKey] = new JsonObject { ["$ref"] = "catalog", ["entryId"] = Payer },
        [InvoiceRequisites.PurposeKey] = "Кабель",
        [InvoiceRequisites.TotalKey] = "1 234,50",
        [InvoiceRequisites.VatTotalKey] = "205,75",
        [InvoiceRequisites.ShippedOnKey] = "2026-10-02",
        [InvoiceRequisites.DeferralKey] = "15",
        [InvoiceRequisites.DueDateKey] = "2026-10-20",
    };

    private static JsonElement Sent(IEnumerable<string> keys)
    {
        var sent = new JsonObject();
        foreach (var key in keys) sent[key] = Typed[key].DeepClone();
        return JsonDocument.Parse(sent.ToJsonString()).RootElement;
    }

    private static JsonObject Stored(InvoiceColumns columns, JsonDocument rest)
    {
        var invoice = Invoice.Create(Guid.NewGuid(), null);
        invoice.ApplyRequisites(columns, rest, dueDateByHand: true, new InvoiceDueRule(true));
        return InvoiceRequisites.Merge(invoice);
    }

    /// <summary>
    /// ⚠️ Сторож полноты: новая колонка в <see cref="InvoiceRequisites.WritableColumnKeys" /> без своего
    /// узла в общем списке колонок доехала бы до охраны текстом — ровно та ошибка, что чинилась. Образца
    /// набранного значения для неё здесь нет — тест красный и называет ключ.
    /// </summary>
    [Fact]
    public void Каждая_колонка_уходит_охране_так_как_ляжет_в_счёт()
    {
        foreach (var key in InvoiceRequisites.WritableColumnKeys)
            Assert.True(Typed.ContainsKey(key), $"Нет образца набранного значения для колонки «{key}» — добавьте его в Typed.");

        var (columns, rest, landing) = InvoiceRequisites.Split(Sent(InvoiceRequisites.WritableColumnKeys), stored: null);
        var stored = Stored(columns, rest);

        foreach (var key in InvoiceRequisites.WritableColumnKeys)
            Assert.True(JsonNode.DeepEquals(stored[key], landing[key]),
                $"«{key}»: охране уходит {landing[key]?.ToJsonString()}, а ляжет {stored[key]?.ToJsonString()}.");
    }

    /// <summary>Тот же набранный текст повторно — не правка: иначе сохранение снимало бы метки распознавания.</summary>
    [Fact]
    public void Набранное_повторно_правкой_не_считается()
    {
        var sent = Sent(InvoiceRequisites.WritableColumnKeys);
        var (columns, rest, _) = InvoiceRequisites.Split(sent, stored: null);
        var stored = Stored(columns, rest);

        var (_, _, again) = InvoiceRequisites.Split(sent, stored);
        Assert.Empty(InvoiceRequisites.Changed(stored, again));
    }

    /// <summary>Неприсланная колонка не появляется: отсутствующий ключ — «стёрто», а выдуманный был бы правкой.</summary>
    [Fact]
    public void Неприсланная_колонка_не_подставляется()
    {
        var (_, _, landing) = InvoiceRequisites.Split(Sent([InvoiceRequisites.TotalKey]), stored: null);

        Assert.Equal([InvoiceRequisites.TotalKey], landing.Select(p => p.Key));
        Assert.Equal(1234.50m, landing[InvoiceRequisites.TotalKey]!.GetValue<decimal>());
    }
}
