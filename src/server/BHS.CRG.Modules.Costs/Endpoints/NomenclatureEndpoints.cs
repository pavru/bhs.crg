using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Номенклатура для строк счёта — ПОИСК по справочнику ядра (задача C2, issue #1078, ТЗ COST-7).
///
/// <para><b>Поиск, а не список — в отличие от организаций.</b> Организаций у заказчика десятки, и они
/// уезжают списком целиком. Номенклатура — самый большой справочник системы: материалы, кабель,
/// оборудование, тысячи позиций, и в данных записи лежат картинки (в одной установке общие данные весили
/// 5,43 МБ, 99,6 % — base64, issue #1015). Список целиком здесь означал бы мегабайты на каждое открытие
/// формы.</para>
///
/// <para>⚠️ <b>Ответ НЕПОЛОН, и это сказано вслух</b> — полем <c>more</c>. Отсечение по числу строк
/// иначе читалось бы как «такой позиции в справочнике нет», а следствие у такого прочтения дорогое:
/// человек заведёт вторую такую же позицию, и сводить затраты после этого придётся вручную.</para>
/// </summary>
public static class NomenclatureEndpoints
{
    /// <summary>
    /// Сколько позиций уезжает на один запрос. Двадцать пять — это список, который человек читает
    /// глазами, не прокручивая: выбор из ста всё равно делают, уточнив запрос, а не листая.
    /// </summary>
    private const int Limit = 25;

    public static void MapNomenclature(IEndpointRouteBuilder endpoints)
    {
        // Правом на счета ИЛИ на накладные (D1, issue #1083): позицию выбирают и в строке накладной, а
        // право на накладные счетов не открывает (COST-29). См. CostsLookups.
        endpoints.MapGet("/api/costs/nomenclature", SearchAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .AddEndpointFilter(CostsLookups.RequireDocumentReader)
            .WithTags("Счета на оплату");
    }

    private static async Task<Ok<NomenclatureSearchResult>> SearchAsync(
        IModuleCatalog catalog, CancellationToken ct, string? query = null)
    {
        var found = await catalog.SearchAsync(CostsRecordTypes.NomenclatureCode, query, Limit + 1, ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.NomenclatureCode}» в системе не заведён, поэтому выбирать " +
                "позицию не из чего. Этот справочник ведёт человек, а появляется он вместе с материалами " +
                "(миграция ядра поднимает «Номенклатуру» над «Материалом» там, где материалы есть). " +
                "Пустой список здесь означал бы «позиций ещё не завели», а это другое.");

        // Спросили на одну больше, чем отдаём: так «есть ли ещё» — это факт, а не догадка по тому,
        // что список заполнился до предела. Полный список ровно в Limit позиций иначе всегда сообщал бы
        // «есть ещё», и человек уточнял бы запрос, которому уточнять нечего.
        // Поиск порта — всегда выбор: архивных позиций в нём нет (issue #1185).
        var more = found.Items.Count > Limit;

        return TypedResults.Ok(new NomenclatureSearchResult(
            [.. found.Items.Take(Limit).Select(r => new NomenclatureItem(r.Id, r.DisplayName, r.EntityType, r.MatchedAlias))],
            more, found.InArchive));
    }
}

/// <summary>Позиция номенклатуры в выборе строки счёта.</summary>
/// <param name="Name">Название позиции; <c>null</c> — у записи его нет вовсе. Подставлять сюда код типа
/// нельзя: список показал бы «Материал» вместо «без названия», то есть соврал бы о данных.</param>
/// <param name="Type">Код типа записи — у подтипа свой («Материал», «Кабель»). Человеку он нужен, чтобы
/// различить похожие позиции, а не для отбора: отбор уже сделан.</param>
/// <param name="MatchedAlias">Альтернативное имя, по которому позиция найдена, — когда набранного нет в
/// названии (issue #1169). Форма показывает его рядом: иначе позиция, в названии которой набранного нет,
/// выглядит ошибкой поиска, и человек заводит дубль рядом с найденным.</param>
public sealed record NomenclatureItem(Guid Id, string? Name, string Type, string? MatchedAlias = null);

/// <summary>Найденное и оговорка о неполноте.</summary>
/// <param name="More">Есть ли ещё подходящие позиции за пределами ответа. Форма обязана сказать это
/// человеку словами: неполный список, выданный за полный, заставляет заводить дубли.</param>
/// <param name="InArchive">Сколько позиций под тот же запрос лежит в архиве (issue #1185). В списке
/// их нет, и форма обязана сказать об этом словами: пустой ответ без оговорки читается как «такой
/// позиции нет» — и человек заводит дубль архивной.</param>
public sealed record NomenclatureSearchResult(IReadOnlyList<NomenclatureItem> Items, bool More, int InArchive);
