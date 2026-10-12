using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
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

        g.MapPut("/", async (AccountingCurrencyRequest req, IAppSettingsStore store,
            IAccountingCurrencyLock guard, IActivityLog journal, AppDbContext db, CancellationToken ct) =>
        {
            var next = req.Currency?.Trim();
            if (!AccountingCurrencies.IsKnown(next))
                return Results.BadRequest(new { error = $"Валюты «{req.Currency}» в списке нет." });

            // Значение, запись журнала и сверка запрета — одной транзакцией. Журнал отдельно от
            // значения оставил бы смену без следа (урок ревью PR #1249).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var was = await store.GetAsync(AppSettingKeys.AccountingCurrency, ct);

            if (AppSettingCatalog.CurrencyChange(was, next) is not { } change)
            {
                // Действует то же самое. Строку правим, только если в базе лежит негодное: это
                // уборка, знак сумм она не меняет — и запрет ей не помеха.
                if (was is not null && was != next)
                {
                    await store.SetAsync(AppSettingKeys.AccountingCurrency, next, ct);
                    await tx.CommitAsync(ct);
                }

                return Results.NoContent();
            }

            // ⚠️ Сначала запереть учётные таблицы, потом сверять (ревью PR #1306). Без замка счёт,
            // зафиксированный между сверкой и фиксацией смены, получил бы новый знак: внесли в
            // рублях — показан в долларах. Замок держится до конца транзакции; вставка, уже идущая
            // рядом, заставит подождать — и сверка её увидит. Создание документа на это время ждёт.
            try { await guard.FreezeAsync(ct); }
            catch (AccountingTablesBusyException busy) { return Results.Conflict(new { error = busy.Message }); }

            var holders = await guard.HoldersAsync(ct);
            if (holders.Count > 0)
                return Results.Conflict(new { error = AccountingCurrencyLockText.Reason(holders) });

            await store.SetAsync(AppSettingKeys.AccountingCurrency, next, ct);
            await journal.RecordAsync(ActivityActions.AccountingCurrencyChanged, AppSettingKeys.AccountingCurrency,
                change.Label, before: change.Before, after: change.After, ct: ct);

            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }
}
