using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Settings;

/// <summary>
/// Валюта учёта глазами администратора (ТЗ COST-7.2, issue #1248).
///
/// <para>Свой адрес, а не поле в <c>PUT /api/settings/company</c>: тот пишет часовой пояс и пустое
/// значение читает как «снять настройку» — экран, приславший туда одну валюту, стёр бы пояс.</para>
///
/// <para>Действующую валюту каждому вошедшему отдаёт <c>GET /api/account/access</c>: форматтер сумм
/// нужен всем, а этот адрес — только тому, кто настройку меняет.</para>
/// </summary>
public static class AccountingCurrencyEndpoints
{
    public record AccountingCurrencyRequest(string? Currency);

    public static void MapAccountingCurrencyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/company/currency")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        g.MapGet("/", async (IAppSettingsStore store, IAccountingCurrencyLock guard, CancellationToken ct) =>
        {
            var stored = await store.GetAsync(AppSettingKeys.AccountingCurrency, ct);
            var holders = await guard.HoldersAsync(ct);
            return Results.Ok(new
            {
                currency = AccountingCurrencies.Effective(stored),
                // В базе лежит строка не из списка — действует рубль, и администратор обязан это
                // увидеть: молча подставленное умолчание выглядело бы выбранным.
                stale = stored is not null && !AccountingCurrencies.IsKnown(stored) ? stored : null,
                // Список отдаёт сервер: второй список на клиенте разошёлся бы с этим молча.
                currencies = AccountingCurrencies.All.Select(c => new { code = c.Code, title = c.Title }),
                locked = holders.Count > 0,
                reason = holders.Count > 0 ? AccountingCurrencyLockText.Reason(holders) : null,
            });
        });

        g.MapPut("/", async (AccountingCurrencyRequest req, IAccountingCurrencyLock guard, AppDbContext db,
            CancellationToken ct) =>
        {
            var next = req.Currency?.Trim();
            if (!AccountingCurrencies.IsKnown(next))
                return Results.BadRequest(new { error = $"Валюты «{req.Currency}» в списке нет." });

            // Значение, запись журнала и сверка запрета — одной транзакцией; что и в каком порядке
            // делается внутри — у самой операции (TryChangeAsync), она же служит восстановлению копии.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            switch (await guard.TryChangeAsync(next!, ct: ct))
            {
                case CurrencyChangeOutcome.Held held:
                    return Results.Conflict(new { error = AccountingCurrencyLockText.Reason(held.Holders) });
                case CurrencyChangeOutcome.Busy:
                    return Results.Conflict(new { error = CurrencyChangeOutcome.Busy.Message });
                default:
                    await tx.CommitAsync(ct);
                    return Results.NoContent();
            }
        });
    }
}
