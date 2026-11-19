using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Offers;

/// <summary>
/// Поля заказчика КП и системные поля типа <c>КоммерческоеПредложение</c> — перекладывание для охраны записи
/// (ТЗ TYPE-17, CORE-20.2), по образцу реквизитов счёта.
///
/// <para>⚠️ <b>Системные «Наценка» и «Итого» в <see cref="Offer.Data" /> не лежат никогда</b>: их значения —
/// колонки, их кладёт код. Охрана записи сверяет их ПРОЕКЦИЕЙ: лежащее — данные плюс значения колонок,
/// присланное — данные, где не названные системные поля дополнены теми же значениями. Названное иначе —
/// отказ охраны «поле заперто» с названием поля; сохраняется только остаток без системных полей.</para>
/// </summary>
internal static class OfferRequisites
{
    public const string MarkupKey = "Наценка";
    public const string TotalKey = "Итого";

    private static readonly string[] SystemKeys = [MarkupKey, TotalKey];

    /// <summary>Как КП лежит — для охраны записи: поля заказчика и проекция системных полей.</summary>
    public static string Stored(Offer offer)
    {
        var node = Parse(offer.Data);
        node[MarkupKey] = offer.Markup;
        node[TotalKey] = offer.Total;
        return node.ToJsonString();
    }

    /// <summary>
    /// Присланные поля заказчика: что спросить у охраны и что сохранить. У правки не названные системные
    /// поля дополняются лежащими — присылать их форма не обязана; у создания (<paramref name="stored" /> —
    /// <c>null</c>) не дополняется ничего: названное системное поле — значение «впервые», и охрана откажет.
    /// </summary>
    public static (string Guarded, JsonElement Customer) Incoming(JsonElement? data, Offer? stored)
    {
        var node = data is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } d
            ? JsonNode.Parse(d.GetRawText()) as JsonObject
              ?? throw new InvalidRequestException("Поля заказчика КП («data») — не JSON-объект.")
            : [];
        if (stored is not null)
        {
            node.TryAdd(MarkupKey, stored.Markup);
            node.TryAdd(TotalKey, stored.Total);
        }

        var guarded = node.ToJsonString();
        foreach (var key in SystemKeys) node.Remove(key);
        return (guarded, JsonSerializer.SerializeToElement(node));
    }

    /// <summary>Охрана записи; отказ — словами КП с названием поля.</summary>
    public static async Task EnsureAllowedAsync(
        IModuleWriteGuard guard, Guid typeId, string? stored, string incoming, CancellationToken ct)
    {
        var refusals = await guard.RefusalsAsync(typeId, stored, incoming, ct);
        if (refusals.Count == 0) return;
        throw new InvalidRequestException("КП не сохранено — охрана записи: " + string.Join(" ", refusals.Select(
            r => r.Path is { Length: > 0 } path && r.Code != "archived-ref" ? $"«{path}»: {r.Message}" : r.Message)));
    }

    /// <summary>Поля заказчика для ответа — как лежат, объектом.</summary>
    public static JsonElement Customer(Offer offer) => JsonSerializer.SerializeToElement(Parse(offer.Data));

    private static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? [];
}
