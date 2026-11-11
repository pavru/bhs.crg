using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Адреса связи «учётная запись → сотрудник» (ТЗ WORK-47, CORE-15, CORE-28; change
/// <c>work-01-employee-account</c>). Стоят в группах двери сотрудников, поэтому ворота у них те же:
/// чтение — <c>core.employees.read</c>, всё остальное — <c>core.employees.edit</c> (ТЗ AUTH-8). Право
/// правки есть только у «Администратора» — решение владельца 11.11.2026: связывает он один.
///
/// <para>Это адреса ядра, а не модуля: ворот модуля у них нет и быть не должно — перепись ворот
/// (<c>EndpointGateInventoryTests</c>) видит их под правом.</para>
/// </summary>
public static class EmployeeAccountEndpoints
{
    /// <param name="edit">Группа правки двери сотрудников — под <c>core.employees.edit</c>.</param>
    public static void MapEmployeeAccountEndpoints(this RouteGroupBuilder edit)
    {
        // ── Связать и отвязать ───────────────────────────────────────────────────────────────
        // Версия — версия КАРТОЧКИ сотрудника заголовком If-Match: связывание её двигает, и форма,
        // открытая до него, получит 409 — карточка показывает связь.
        edit.MapPut("/{id:guid}/account", async (Guid id, LinkAccountRequest req, HttpRequest http,
            EmployeeAccountLinks links, EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            if (RecordIfMatch.Refuse(http, out var seen) is { } refused) return refused;
            if (req.UserId == Guid.Empty)
                return Results.BadRequest(new { error = "В запросе нет учётной записи (userId)." });
            return await AnswerAsync(await links.LinkAsync(id, req.UserId, seen, ct), accounts, ct);
        });

        edit.MapDelete("/{id:guid}/account", async (Guid id, HttpRequest http,
            EmployeeAccountLinks links, EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            if (RecordIfMatch.Refuse(http, out var seen) is { } refused) return refused;
            return await AnswerAsync(await links.UnlinkAsync(id, seen, ct), accounts, ct);
        });

        // ── Пачкой: режим «Сопоставить» ───────────────────────────────────────────────────────
        // Версия здесь — в строке тела, а не заголовком: у пачки версий столько, сколько строк, и
        // If-Match их не вместит. Названное исключение из правила заголовка (спека core).
        edit.MapPost("/account-links", async (LinkAccountsRequest req,
            EmployeeAccountLinks links, CancellationToken ct) =>
        {
            if (req.Links is not { Length: > 0 } rows)
                return Results.BadRequest(new { error = "В пачке нет ни одной строки." });
            if (rows.Length > EmployeeAccountLinks.BatchMax)
                return Results.BadRequest(new { error =
                    $"В пачке {rows.Length} строк, а за раз связывается не больше {EmployeeAccountLinks.BatchMax}. " +
                    "Разделите её — удачные строки первой части останутся связанными." });
            // Пачка не по форме отвергается ЦЕЛИКОМ: строка без версии — это не отказ строки, а запрос,
            // собранный не так, и связать «остальные» значило бы угадывать, что имелось в виду.
            // Версия строки читается тем же правилом, что заголовок If-Match: в кавычках — та же версия,
            // а «*» и не число — запрос не по форме, а не ложное «тем временем изменили».
            var parsed = rows.Select(r => (Row: r, Ok: SeenVersion.TryParse(r.Version, out var v), Version: v)).ToList();
            if (parsed.Any(p => p.Row.EmployeeId == Guid.Empty || p.Row.UserId == Guid.Empty || !p.Ok || p.Version is null))
                return Results.BadRequest(new { error =
                    "Каждая строка пачки обязана назвать сотрудника, учётную запись и версию карточки " +
                    "(поле version из ответа чтения — число). Не связано ничего." });

            var results = await links.LinkManyAsync(
                [.. parsed.Select(p => (p.Row.EmployeeId, p.Row.UserId, p.Version!))], ct);
            return Results.Ok(new
            {
                linked = results.Count(r => r.Result.Succeeded),
                total = results.Count,
                results = results.Select(r => new
                {
                    employeeId = r.EmployeeId,
                    outcome = r.Result.Outcome,
                    error = r.Result.Error,
                    linkedTo = r.Result.LinkedTo,
                    accountName = r.Result.AccountName,
                    version = r.Result.Employee?.Version,
                }),
            });
        });

        // ── Кандидаты и предложения ───────────────────────────────────────────────────────────
        // Под ПРАВКОЙ, а не чтением: список учётных записей нужен только тому, кто связывает.
        edit.MapGet("/account-candidates", async (string? q, EmployeeAccountReads accounts, CancellationToken ct) =>
        {
            var (items, more) = await accounts.CandidatesAsync(q, ct);
            return Results.Ok(new { items, more });
        });

        edit.MapGet("/account-matches", async (EmployeeAccountReads accounts, CancellationToken ct) =>
            await accounts.MatchesAsync(ct) is { } rows
                ? Results.Ok(rows)
                : Results.Conflict(new { code = EmployeeAccountReads.DirectoryMissing, error = DirectoryMissingText }));

        // ── «Завести» справочник ──────────────────────────────────────────────────────────────
        edit.MapPost("/directory", async (IMediator m, CancellationToken ct) =>
        {
            var outcome = await m.Send(new CreateEmployeeDirectoryCommand(), ct);
            if (outcome.TypeId is { } typeId) return Results.Ok(new { typeId, created = outcome.Created });
            return Results.Conflict(new
            {
                code = EmployeeAccountReads.DirectoryMissing,
                reason = outcome.Refusal,
                error = outcome.Refusal == EmployeeDirectoryOutcome.PersonMissing ? PersonMissingText : BlockedText,
            });
        });
    }

    /// <summary>Итог связывания или отвязывания — ответом адреса.</summary>
    private static async Task<IResult> AnswerAsync(LinkResult result, EmployeeAccountReads accounts, CancellationToken ct)
    {
        if (result.Succeeded) return Results.Ok(await EmployeeEntryDto.OfAsync(result.Employee!, accounts, ct));
        if (result.Outcome == LinkResult.NotFound.Outcome) return Results.NotFound();
        return Results.Conflict(new
        {
            code = result.Outcome, error = result.Error, linkedTo = result.LinkedTo, accountName = result.AccountName,
        });
    }

    internal const string DirectoryMissingText =
        "Справочник сотрудников ещё не заведён. Сопоставлять не с кем: сначала заведите справочник.";

    private const string PersonMissingText =
        "Справочник сотрудников не заведён: нет типа «Персона», от которого он производен. Заведите " +
        "«Персону» в «Типах документов» и нажмите «Завести» снова.";

    private const string BlockedText =
        "Справочник сотрудников не заведён: код или имя «Сотрудник» заняты другим типом. Переименуйте " +
        "тот тип в «Типах документов» и нажмите «Завести» снова.";

    private sealed record LinkAccountRequest(Guid UserId);

    private sealed record LinkAccountsRequest(LinkRow[]? Links);

    private sealed record LinkRow(Guid EmployeeId, Guid UserId, string? Version);
}

/// <summary>
/// Сотрудник в выдаче двери — карточка и состояние связи с учётной записью (ТЗ WORK-47). Связь — из
/// таблицы ядра, а не из поля <c>УчётнаяЗапись</c> карточки: то поле заперто и связью не заполняется.
/// </summary>
public sealed record EmployeeEntryDto : CommonDataEntryDto
{
    public EmployeeEntryDto(CommonDataEntryDto entry, EmployeeAccountDto account) : base(entry) => Account = account;

    public EmployeeAccountDto Account { get; init; }

    public static async Task<EmployeeEntryDto> OfAsync(DomainObject entry, EmployeeAccountReads accounts, CancellationToken ct) =>
        new(CommonDataEntryDto.From(entry), (await accounts.ForAsync([entry], ct))[entry.Id]);
}
