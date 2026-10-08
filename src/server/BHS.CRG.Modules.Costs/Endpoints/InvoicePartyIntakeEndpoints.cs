using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Название, под которым завести организацию; пусто — как прочитано в скане.</summary>
public sealed record PartyOrganizationRequest(string? Name);

/// <param name="Created">Заведённая этим запросом организация; <c>null</c> — заводить не пришлось:
/// она уже есть (её тем временем завёл кто-то ещё), и что с ней делать, говорит <paramref name="Party" />.</param>
/// <param name="Party">Сторона счёта, сопоставленная ЗАНОВО, после заведения.</param>
public sealed record PartyOrganizationView(Guid? Created, InvoicePartyView Party);

/// <summary>
/// Завести организацию, которую прочитали в скане счёта и не нашли в справочнике (ТЗ COST-8, задача
/// B1b, issue #1077).
///
/// <para>⚠️ <b>ИНН берётся из сохранённого распознавания ЭТОГО счёта, а не из запроса.</b> На этом
/// стоит всё право: оно означает «завести организацию, прочитанную в скане», а не «завести любую».
/// Прими адрес ИНН из тела — и право модуля стало бы правом вести справочник организаций. Название
/// из тела принять можно: модель читает его с кавычками и переносами, и человек вправе поправить.</para>
///
/// <para>⚠️ <b>Заводится только при ответе «в справочнике нет».</b> Любое другое состояние стороны
/// значит, что утверждать «её нет» нельзя: ИНН прочитан с ошибкой, у части записей он не читается,
/// справочник не ответил. Завести по такому ответу — завести дубль.</para>
///
/// <para>Счёт этот адрес НЕ меняет и версию его (<c>If-Match</c>) не спрашивает: заведённую
/// организацию в поле ставит человек обычной правкой шапки. Так кнопка не затирает то, что он успел
/// выбрать, а устаревшая форма ничего не теряет.</para>
/// </summary>
public static class InvoicePartyIntakeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/costs/invoices/{id:guid}/recognition/parties/{side}/organization", CreateAsync)
            .RequireAuthorization(AppPolicies.Permission(CostsModule.OrganizationCreate))
            .WithTags("Счета на оплату");

    private static async Task<Ok<PartyOrganizationView>> CreateAsync(
        Guid id, string side, PartyOrganizationRequest? body, CostsDbContext db, InvoiceScanRecognition scan,
        InvoiceParties parties, IModuleCatalogIntake intake, IModuleActivityLog log, CancellationToken ct)
    {
        if (side is not ("supplier" or "payer"))
            throw new InvalidRequestException($"Стороны «{side}» у счёта нет: поставщик — supplier, плательщик — payer.");

        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        if (invoice.State != InvoiceState.Draft)
            throw new ConflictException(
                "Организацию из скана заводят, пока счёт — черновик. У разобранного счёта стороны уже выбраны.");

        var read = await scan.ReadValuesAsync(invoice, ct)
            ?? throw new ConflictException(
                "Скан этого счёта не распознан — заводить организацию не по чему. Распознайте скан или " +
                "заведите организацию в справочнике.");

        var party = Pick(await parties.MatchAsync(read, ct), side)
            ?? throw new ConflictException("Про эту сторону в скане ничего не прочитано — заводить нечего.");

        // Уже есть (завёл кто-то ещё, пока форма была открыта) — не отказ: форма получает сторону
        // как она есть сейчас и предлагает найденное.
        if (party.State is InvoicePartyStates.Matched or InvoicePartyStates.Several or InvoicePartyStates.Archived)
            return TypedResults.Ok(new PartyOrganizationView(null, party));
        if (party.State != InvoicePartyStates.Absent || party.TaxId is not { } taxId)
            throw new ConflictException(
                "Организацию по этому скану завести нельзя: " + (party.Why ?? "неизвестно, есть ли она в справочнике") +
                " Заведите её в справочнике организаций.");

        var name = string.IsNullOrWhiteSpace(body?.Name) ? party.Name?.Trim() : body.Name.Trim();
        if (string.IsNullOrEmpty(name))
            throw new InvalidRequestException("Название организации в скане не прочитано — введите его.");

        var result = await intake.CreateAsync(CostsRecordTypes.OrganizationCode, name, taxId, ct)
            ?? throw new ConflictException(
                $"Типа «{CostsRecordTypes.OrganizationCode}» в системе нет — организацию завести нечем.");
        if (result.Refusals.Count > 0)
            throw new ConflictException(
                "Организацию из скана завести не удалось — мешает схема типа «Организация»: " +
                string.Join("; ", result.Refusals) + ". Заведите организацию в справочнике.");

        if (result.Created is { } created)
            await log.RecordAsync(InvoiceActions.OrganizationCreated, created.Id.ToString(), name,
                after: $"ИНН {taxId}; {InvoiceEndpoints.Label(invoice)}", ct: CancellationToken.None);

        // Сторона — заново: теперь она «найдена», и форма узнаёт об этом тем же видом, что при чтении.
        var now = Pick(await parties.MatchAsync(read, ct), side) ?? party;
        return TypedResults.Ok(new PartyOrganizationView(result.Created?.Id, now));
    }

    private static InvoicePartyView? Pick(InvoicePartiesView view, string side) =>
        side == "supplier" ? view.Supplier : view.Payer;
}
