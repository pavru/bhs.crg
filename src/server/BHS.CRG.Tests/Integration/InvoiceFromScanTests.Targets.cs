using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт из файла: шапка по правилу «ключ поля профиля = ключ поля счёта» (эпик #1479, issue #1483).
/// Поля заказчика дописываются в тип «Счёт на оплату» на время теста и убираются после: тип общий для
/// всех тестов стенда. Числа выдуманные.
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    /// <summary>Дописать поля в тип счёта — как администратор в редакторе; по выходу схема возвращается.</summary>
    private async Task<IAsyncDisposable> ExtendInvoiceTypeAsync(params JsonObject[] fields)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var type = await db.DocumentTypes.SingleAsync(t => t.Code == CostsRecordTypes.InvoiceCode);
        var original = type.Schema.RootElement.GetRawText();
        var root = JsonNode.Parse(original)!.AsObject();
        foreach (var field in fields) root["fields"]!.AsArray().Add(field);
        type.UpdateSchema(JsonDocument.Parse(root.ToJsonString()));
        await db.SaveChangesAsync();
        return new Restore(host, type.Id, original);
    }

    private sealed class Restore(InvoiceScanHost host, Guid typeId, string schema) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DocumentTypes.SingleAsync(t => t.Id == typeId)).UpdateSchema(JsonDocument.Parse(schema));
            await db.SaveChangesAsync();
        }
    }

    private async Task<Guid> PrimitiveAsync(string code, string pattern, string message)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var primitive = PrimitiveType.Create($"Тест {code}", code, "string", null,
            JsonDocument.Parse(new JsonObject { ["pattern"] = pattern, ["patternMessage"] = message }.ToJsonString()));
        db.PrimitiveTypes.Add(primitive);
        await db.SaveChangesAsync();
        return primitive.Id;
    }

    private static JsonObject Field(string key, string type, Guid? typeId = null)
    {
        var field = new JsonObject { ["key"] = key, ["title"] = key, ["type"] = type };
        if (typeId is { } id) field["typeId"] = id.ToString();
        return field;
    }

    private static IEnumerable<string> HeaderNotes(JsonElement recognition) =>
        recognition.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!);

    /// <summary>
    /// Поле, дописанное заказчиком, заполняется колонкой профиля с тем же ключом — строка и число по виду.
    /// Значение, которое охрана отвергла (шаблон типа поля), уходит в предложение с её словами, а
    /// остальное ложится: один отказ не роняет распознавание целиком.
    /// </summary>
    [Fact]
    public async Task Поле_заказчика_заполняется_а_отказ_охраны_по_полю_не_роняет_остальное()
    {
        var code = $"contract{Guid.NewGuid():N}"[..20];
        var primitive = await PrimitiveAsync(code, "^Д-\\d+$", "Код договора — «Д-» и цифры");
        await using var extended = await ExtendInvoiceTypeAsync(
            Field("Договор1483", "string"), Field("Объём1483", "number"), Field("Код1483", "primitive", primitive));

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var header = Header(number: "СЧ-1483-1", total: "1 000,00");
        header["Договор1483"] = "№ 12/7";
        header["Объём1483"] = "12,5";
        header["Код1483"] = "X-1";
        host.Recognition.On(scan, () => Task.FromResult(Read(header)));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        var requisites = view.GetProperty("requisites");
        Assert.Equal("№ 12/7", requisites.GetProperty("Договор1483").GetString());
        Assert.Equal(12.5m, requisites.GetProperty("Объём1483").GetDecimal());
        Assert.Equal("СЧ-1483-1", requisites.GetProperty("Номер").GetString());
        Assert.False(requisites.TryGetProperty("Код1483", out var refused) && refused.ValueKind != JsonValueKind.Null);
        Assert.Contains("Договор1483", Unconfirmed(view));
        Assert.Contains("Объём1483", Unconfirmed(view));
        Assert.DoesNotContain("Код1483", Unconfirmed(view));

        var offer = recognition.GetProperty("offers").GetProperty("Код1483");
        Assert.Equal("X-1", offer.GetProperty("text").GetString());
        Assert.Equal("X-1", offer.GetProperty("value").GetString());
        Assert.Contains(HeaderNotes(recognition), n => n.Contains("Код1483") && n.Contains("охрана записи"));
    }

    /// <summary>
    /// Правленая копия со старым ключом и новый ключ в одном ответе: побеждает точный, второе названо.
    /// </summary>
    [Fact]
    public async Task Точный_ключ_старше_прежнего()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var header = Header(number: "СЧ-1483-2a");
        header["Номер"] = "СЧ-1483-2b";
        host.Recognition.On(scan, () => Task.FromResult(Read(header)));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);

        Assert.Equal("СЧ-1483-2b", (await ReadAsync(client, id)).GetProperty("requisites").GetProperty("Номер").GetString());
        Assert.Contains(HeaderNotes(recognition), n => n.Contains("два поля профиля") && n.Contains("СЧ-1483-2a"));
    }

    /// <summary>
    /// «Назначение» со скана не заполняется (решение владельца 18.10.2026), но колонка профиля с этим
    /// ключом заполняет его, как любое поле. Ключ с опечаткой назван в замечаниях, а не потерян молча.
    /// </summary>
    [Fact]
    public async Task Назначение_своим_ключом_а_опечатка_названа()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var header = Header(number: "СЧ-1483-3", basis: "Договор № 9");
        header["Назначение"] = "кабель для СКС";
        header["Основанье"] = "опечатка";
        host.Recognition.On(scan, () => Task.FromResult(Read(header)));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);

        var requisites = (await ReadAsync(client, id)).GetProperty("requisites");
        Assert.Equal("кабель для СКС", requisites.GetProperty("Назначение").GetString());
        Assert.Equal("Договор № 9", requisites.GetProperty("Основание").GetString());
        Assert.Contains(HeaderNotes(recognition), n => n.Contains("«Основанье» «опечатка»") && n.Contains("нет поля с таким ключом"));
    }
}
