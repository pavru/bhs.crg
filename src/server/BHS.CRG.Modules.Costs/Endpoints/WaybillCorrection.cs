using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Исправление накладной закрытого периода (issue #1340, ТЗ COST-16, CORE-36): одно действие — причина и
/// всё содержимое; прежнее уходит версией (<see cref="WaybillVersion" />), накладная остаётся проведённой.
///
/// <para><b>Единственное место, которое пишет и читает историю накладной</b> (перепись
/// <c>RecordVersionInventoryTests</c>): запись исправления, адрес версий и пометка списка. «Материалы на
/// объекте» и диалог закрытия к истории не обращаются — они читают действующее содержимое.</para>
///
/// <para><b>Ворота — правка накладных</b> (<c>costs.waybill.edit</c>), а право исправления спрашивает
/// связка, ПОСЛЕ «исправление не нужно» (<see cref="WaybillDesk.CorrectAsync{T}" />), как у счёта.</para>
///
/// <para><b>Без предпросмотра и отметки</b> (D2): у счёта они сторожат книгу проводок — «записать не то, что
/// человек видел». У накладной книги нет, и хватает версии формы.</para>
/// </summary>
public static class WaybillCorrection
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/waybills/{id:guid}").WithTags("Расходные накладные");

        group.MapPost("/correction", CorrectAsync).RequireAuthorization(AppPolicies.Permission("costs.waybill.edit"));
        group.MapGet("/versions", VersionsAsync).RequireAuthorization(AppPolicies.Permission("costs.waybill.read"));
    }

    /// <summary>
    /// Исправить запертую накладную. Тело — как у правки черновика (шапка и «lines») плюс «reason». Журнал —
    /// после транзакции: он пишется соединением ядра и зафиксировался бы раньше модуля.
    /// </summary>
    private static async Task<Ok<WaybillView>> CorrectAsync(
        Guid id, JsonElement body, CostsDbContext db, IModuleConstructions sites, IModuleCatalog catalog,
        IModuleUser user, IModuleActivityLog log, WaybillDesk desk, CancellationToken ct)
    {
        var reason = Reason(body);
        var header = WaybillRequests.Header(body);
        var lines = WaybillRequests.Lines(body)
            ?? throw new InvalidRequestException(
                "Набор строк не прислан. Исправление заменяет содержимое накладной целиком, и отсутствие поля " +
                "прочитать как «строки не менять» нельзя.");
        var known = await sites.ListAsync(ct);

        var (waybill, number, siteMoved, dayMoved) = await desk.CorrectAsync(id, async write =>
        {
            var waybill = write.Waybill;
            WaybillEndpoints.EnsureSiteExists(known, header.ConstructionId);
            EnsurePostable(waybill, header, lines);
            // Прежние строки читаются один раз: из них и версия, и позиции, которые правило записи не перепроверяет.
            var was = await db.WaybillLines.AsNoTracking().Where(l => l.WaybillId == waybill.Id).ToListAsync(ct);
            await WaybillEndpoints.EnsureNomenclatureAsync(catalog, WaybillEndpoints.Numbered(lines),
                [.. was.Where(l => l.NomenclatureId is not null).Select(l => l.NomenclatureId!.Value).Distinct()], ct);

            var content = WaybillVersion.Snapshot(waybill, was, user.Name);
            var number = waybill.CurrentVersion;
            var siteWas = waybill.ConstructionId;
            // День, которым накладная лежит в учёте. Без учётной даты (накладная из прежней копии) это дата
            // отпуска — так её и запирает WaybillLock; запертая проведённая без обеих не бывает.
            var dayWas = waybill.AccountingOn ?? waybill.IssuedOn!.Value;

            var headerChanged = waybill.Header() != header;
            if (headerChanged) waybill.Apply(header);
            if (!await WaybillEndpoints.PlaceLinesAsync(db, waybill, lines, ct) && !headerChanged)
                throw new InvalidRequestException(
                    $"{WaybillEndpoints.Label(waybill)}: исправление ничего не меняет — шапка и строки те же. " +
                    "Версия без изменений объясняла бы то, чего не было.");

            // Учётная дата не двигается: это день, которым накладная легла в учёт (D4). Два исключения. Смена
            // стройки — накладная ложится в первый открытый день новой стройки (COST-16). Дата отпуска позже
            // прежнего дня — учтена она не раньше отпуска (то же правило), то есть днём отпуска: материалы,
            // учтённые раньше, чем выданы, — неправда. Накладная без учётной даты её получает: иначе
            // исправленная дата отпуска молча перенесла бы её в другой период.
            var siteMoved = waybill.ConstructionId != siteWas;
            var day = siteMoved
                ? WaybillLock.AccountedOn(waybill.IssuedOn!.Value, waybill.ConstructionId!.Value, write.Boundaries)
                : waybill.IssuedOn!.Value > dayWas ? waybill.IssuedOn.Value : dayWas;
            var dayMoved = day != dayWas;
            if (day != waybill.AccountingOn) waybill.Reaccount(day);

            db.WaybillVersions.Add(WaybillVersion.Create(waybill.Id, number, content, reason, user.Id));
            waybill.Corrected();
            await db.SaveChangesAsync(ct);
            return (waybill, number, siteMoved, dayMoved);
        }, ct);

        await log.RecordAsync(WaybillActions.Corrected, waybill.Id.ToString(), WaybillEndpoints.Label(waybill),
            before: $"версия {number}",
            after: reason + (siteMoved ? $"; стройка сменена, учтена {waybill.AccountingOn:dd.MM.yyyy}"
                : dayMoved ? $"; дата отпуска позже прежнего дня учёта, учтена {waybill.AccountingOn:dd.MM.yyyy}" : ""), ct: ct);

        return TypedResults.Ok(await WaybillEndpoints.ViewAsync(waybill, db, known, catalog, desk, ct));
    }

    /// <summary>Версии накладной, свежие сверху. Накладной нет — 404, а не пустой список.</summary>
    private static async Task<Ok<IReadOnlyList<WaybillVersionView>>> VersionsAsync(
        Guid id, CostsDbContext db, CancellationToken ct)
    {
        if (!await db.Waybills.AnyAsync(w => w.Id == id, ct))
            throw new NotFoundException("Накладная не найдена.");

        var versions = await db.WaybillVersions.AsNoTracking().Where(v => v.RecordId == id)
            .OrderByDescending(v => v.Number).ToListAsync(ct);

        return TypedResults.Ok<IReadOnlyList<WaybillVersionView>>([.. versions.Select(v =>
        {
            // Элемент без документа за спиной: JsonDocument пришлось бы освобождать, а Clone этого не делает.
            var content = JsonSerializer.Deserialize<JsonElement>(v.Content);
            var author = content.TryGetProperty("authorName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null;
            return new WaybillVersionView(v.Number, v.Reason, v.AuthorId, author, v.At, content);
        })]);
    }

    /// <summary>Последнее исправление каждой из названных накладных — для пометки в списке.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, WaybillCorrectionMark>> MarksAsync(
        CostsDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, WaybillCorrectionMark>();

        return await db.WaybillVersions.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId)
                        && !db.WaybillVersions.Any(n => n.RecordId == v.RecordId && n.Number > v.Number))
            .ToDictionaryAsync(v => v.RecordId, v => new WaybillCorrectionMark(v.At, v.Reason), ct);
    }

    private static string Reason(JsonElement body)
    {
        CostsValues.EnsureObject(body, "Исправление", "исправления");
        return CorrectionReason.Checked(CostsValues.Text(body, "reason", "Причина исправления"), "накладной");
    }

    /// <summary>
    /// Исправленная накладная остаётся проведённой — и обязана остаться той, что можно провести: без даты,
    /// стройки и количества выданное нечем посчитать (правило проведения, <c>WaybillEndpoints.PostAsync</c>).
    /// </summary>
    private static void EnsurePostable(
        Waybill waybill, WaybillHeader header, IReadOnlyList<(Guid? Id, WaybillLineValues Values)> lines)
    {
        var problems = new List<string>();
        if (header.Number is null) problems.Add("не назван номер");
        if (header.IssuedOn is null) problems.Add("не названа дата отпуска");
        if (header.ConstructionId is null) problems.Add("не названа стройка-получатель");
        if (lines.Count == 0) problems.Add("нет ни одной строки");

        var empty = lines.Select((l, index) => (l.Values.Quantity, Number: index + 1))
            .Where(l => l.Quantity is null or 0).Select(l => l.Number).ToList();
        if (empty.Count > 0)
            problems.Add($"не названо количество: {(empty.Count == 1 ? "строка" : "строки")} {string.Join(", ", empty)}");

        if (problems.Count > 0)
            throw new InvalidRequestException(
                $"{WaybillEndpoints.Label(waybill)}: {string.Join("; ", problems)}. Исправленная накладная остаётся " +
                "проведённой, а проведённая означает, что материалы выданы на стройку.");
    }
}
