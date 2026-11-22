using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

public partial class InvoiceDuplicatePaymentTests
{
    private const long LockKey = 0x434F53544455504C;

    [Fact]
    public async Task Оплата_ждёт_появления_двойника_и_перепроверяет_запрет_после_предпросмотра()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, UniqueNumber());
        var (site, _) = await SiteAsync("Счёт перед появлением двойника");
        await AllocateAsync(client, one, LineId(await ReadAsync(client, one), 1), [Part(site, quantity: 1)]);
        var today = await TodayAsync();
        var view = await ReadAsync(client, one);
        var version = Version(view);
        var seen = Stamp(await PreviewAsync(client, one, today));
        Assert.NotEmpty(seen);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE costs.invoices SET number = {number} WHERE id = {other}");

        var paying = PayAsync(client, one, today, seen, version);
        await WaitForLockAsync(db, "ShareLock");
        Assert.False(paying.IsCompleted);
        await tx.CommitAsync();
        using var refused = await paying;
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var after = await ReadAsync(client, one);
        Assert.Equal(version, Version(after));
        Assert.False(Payment(after).GetProperty("paid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Payment(after).GetProperty("paidOn").ValueKind);
        Assert.Equal("duplicate", (await PreviewAsync(client, one, today)).GetProperty("refusalKind").GetString());
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == one);
        Assert.Null(invoice.RemainderAccountingOn);
        Assert.Null((await db.InvoiceAllocations.AsNoTracking().SingleAsync(part => part.InvoiceId == one)).AccountingOn);
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        Assert.DoesNotContain(await journal.ExportAsync(), r => r.Action == "costs.invoice.paid" && r.TargetId == one.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Исключительная_правка_ждёт_разделяемую_и_продолжается_после_фиксации_или_отката(bool commit)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateNamedAsync(client, UniqueNumber());
        var requisites = await RequisitesWithAsync(client, invoice, "Номер", UniqueNumber());
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared({LockKey})");

        var editing = client.PutAsJsonAsync(Url(invoice), new { requisites });
        await WaitForLockAsync(db, "ExclusiveLock");
        Assert.False(editing.IsCompleted);
        if (commit) await tx.CommitAsync();
        else await tx.RollbackAsync();
        await OkAsync(await editing);
    }

    [Fact]
    public async Task Редакция_переживает_два_имени_одних_байтов_и_старый_файл_без_отпечатка()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, number);
        const string content = "%PDF-1.4\nИзначальный скан";
        await AttachAsync(client, other, "счёт.pdf", content);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE costs.invoices SET scan_fingerprint = NULL, scan_fingerprint_seal = NULL WHERE id = {other}");
        var before = await ReadAsync(client, one);
        var revision = Revision(before, other);
        await OkAsync(await ConfirmAsync(client, one, Version(before), other, revision, "Разные поставки"));
        await AttachAsync(client, other, "счёт с другим именем.pdf", content);
        Assert.Empty(Blockers(await ReadAsync(client, one)));
        await AttachAsync(client, other, "счёт с другим именем.pdf", content);
        Assert.Empty(Blockers(await ReadAsync(client, one)));

        await AttachAsync(client, other, "счёт с другим именем.pdf", "%PDF-1.4\nИсправленный скан");
        Assert.NotEqual(revision, Revision(await ReadAsync(client, one), other));
        var current = await ReadAsync(client, one);
        await OkAsync(await ConfirmAsync(client, one, Version(current), other, Revision(current, other), "Перепроверено"));
        await UpdateAsync(client, other, "Назначение", "Поставку разделили по складам");
        Assert.Empty(Blockers(await ReadAsync(client, one)));
        await AttachAsync(client, one, "первый.pdf", content);
        current = await ReadAsync(client, one);
        Assert.Equal(other, Blocker(current).GetProperty("id").GetGuid());
        await OkAsync(await ConfirmAsync(client, one, Version(current), other, Revision(current, other), "Приложен оригинал"));
        await UpdateAsync(client, one, "Поставщик", Reference(payer));
        await UpdateAsync(client, one, "Поставщик", Reference(supplier));
        current = await ReadAsync(client, one);
        await OkAsync(await ConfirmAsync(client, one, Version(current), other, Revision(current, other), "Поставщик уточнён"));
        await UpdateAsync(client, one, "Номер", UniqueNumber());
        await UpdateAsync(client, one, "Номер", number);
        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Копия_возвращает_подтверждение_а_обновление_старой_копией_аннулирует_его()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, number);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var backup = scope.ServiceProvider.GetRequiredService<IModuleSchemaBackup>();
        var beforeConfirmation = await ReadInvoiceBackupAsync(db, backup, one, other);
        var view = await ReadAsync(client, one);
        await OkAsync(await ConfirmAsync(client, one, Version(view), other, Revision(view, other), "Разные заказы"));
        var snapshot = await ReadInvoiceBackupAsync(db, backup, one, other);

        var edited = await RequisitesWithAsync(client, other, "Номер", UniqueNumber());
        await OkAsync(await client.PutAsJsonAsync(Url(other), new { requisites = edited }));
        await RestoreAsync(beforeConfirmation);
        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());
        Assert.Equal(one, Blocker(await ReadAsync(client, other)).GetProperty("id").GetGuid());
        await RestoreAsync(snapshot);
        Assert.Empty(Blockers(await ReadAsync(client, one)));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM costs.invoice_duplicate_confirmations WHERE first_invoice_id IN ({one}, {other})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM costs.invoices WHERE id IN ({one}, {other})");
        await RestoreAsync(snapshot);
        Assert.Empty(Blockers(await ReadAsync(client, one)));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO costs.invoice_duplicate_confirmations
            SELECT gen_random_uuid(), first_invoice_id, first_revision, second_invoice_id, second_revision,
                first_label, second_label, author_id, author_name, confirmed_at, reason, journaled_at
            FROM costs.invoice_duplicate_confirmations WHERE first_invoice_id IN ({one}, {other})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var reverse = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO costs.invoice_duplicate_confirmations
            SELECT gen_random_uuid(), second_invoice_id, second_revision, first_invoice_id, first_revision,
                second_label, first_label, author_id, author_name, confirmed_at, reason, journaled_at
            FROM costs.invoice_duplicate_confirmations WHERE first_invoice_id IN ({one}, {other})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reverse.SqlState);

        var invoices = snapshot.Tables.Single(table => table.Table == "invoices");
        var row = JsonNode.Parse(invoices.Rows.Single(r => r.GetProperty("id").GetGuid() == other).GetRawText())!.AsObject();
        row.Remove("duplicate_revision");
        row["number"] = UniqueNumber();
        await RestoreAsync(snapshot with { Tables = [invoices with { Rows = [JsonSerializer.SerializeToElement(row)] }] });
        Assert.Empty(Blockers(await ReadAsync(client, one)));
        row["number"] = number;
        await RestoreAsync(snapshot with { Tables = [invoices with { Rows = [JsonSerializer.SerializeToElement(row)] }] });
        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());

        async Task RestoreAsync(BackupModuleSchema data)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var restored = await backup.RestoreAsync([data], tx.GetDbTransaction(), [], default);
            Assert.NotEmpty(restored.Restored);
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task Оплата_ждёт_импорта_копии_и_видит_восстановленного_оплаченного_двойника()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var other = await CreateNamedAsync(client, number);
        var (site, _) = await SiteAsync("Счёт из копии");
        await AllocateAsync(client, other, LineId(await ReadAsync(client, other), 1), [Part(site, quantity: 1)]);
        var today = await TodayAsync();
        await OkAsync(await PayAsync(client, other, today, Stamp(await PreviewAsync(client, other, today)),
            Version(await ReadAsync(client, other))));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var backup = scope.ServiceProvider.GetRequiredService<IModuleSchemaBackup>();
        var snapshot = await ReadInvoiceBackupAsync(db, backup, other);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE costs.invoices SET number = {UniqueNumber()} WHERE id = {other}");

        var one = await CreateNamedAsync(client, number);
        await AllocateAsync(client, one, LineId(await ReadAsync(client, one), 1), [Part(site, quantity: 1)]);
        var version = Version(await ReadAsync(client, one));
        var seen = Stamp(await PreviewAsync(client, one, today));
        Assert.NotEmpty(seen);

        await using var tx = await db.Database.BeginTransactionAsync();
        var restored = await backup.RestoreAsync([snapshot], tx.GetDbTransaction(), [], default);
        Assert.NotEmpty(restored.Restored);
        var paying = PayAsync(client, one, today, seen, version);
        await WaitForLockAsync(db, "ShareLock");
        Assert.False(paying.IsCompleted);
        await tx.CommitAsync();

        using var refused = await paying;
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var after = await ReadAsync(client, one);
        Assert.False(Payment(after).GetProperty("paid").GetBoolean());
        Assert.True(Blocker(after).GetProperty("paid").GetBoolean());
        Assert.Equal(other, Blocker(after).GetProperty("id").GetGuid());
    }

    private static async Task<BackupModuleSchema> ReadInvoiceBackupAsync(
        CostsDbContext db, IModuleSchemaBackup backup, params Guid[] invoices)
    {
        await using var read = await db.Database.BeginTransactionAsync();
        var schema = Assert.Single(await backup.ReadAsync(read.GetDbTransaction(), [], default));
        await read.CommitAsync();
        return schema with
        {
            Tables = [.. schema.Tables.Where(table => table.Table is "invoices" or "invoice_duplicate_confirmations")
                .Select(table => table with { Rows = [.. table.Rows.Where(row => table.Table == "invoices"
                    ? invoices.Contains(row.GetProperty("id").GetGuid())
                    : invoices.Contains(row.GetProperty("first_invoice_id").GetGuid())
                        && invoices.Contains(row.GetProperty("second_invoice_id").GetGuid()))] })],
            Files = null,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Повтор_доставляет_исходное_событие_после_сбоя(bool afterAppend)
    {
        var (client, author) = await SignInAsync("Admin");
        var (retryClient, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, number);
        var av = await ReadAsync(client, one);
        var bv = await ReadAsync(retryClient, other);
        using var failing = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IModuleDeferredActivityLog>();
            services.AddScoped<IModuleDeferredActivityLog>(provider =>
                new FailingDelivery(ActivatorUtilities.CreateInstance<ModuleActivityLogPort>(provider), afterAppend));
        }));
        using var faultClient = failing.CreateClient();
        faultClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        await OkAsync(await ConfirmAsync(faultClient, one, Version(av), other, Revision(av, other), "Исходная причина"));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var pending = await db.InvoiceDuplicateConfirmations.AsNoTracking().SingleAsync(
            saved => saved.FirstInvoiceId == one || saved.SecondInvoiceId == one);
        Assert.Null(pending.JournaledAt);
        await OkAsync(await ConfirmAsync(retryClient, other, Version(bv), one, Revision(bv, one), "Причина повтора"));
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var entry = Assert.Single(await journal.ExportAsync(), r => r.Id == pending.EventId);
        Assert.Equal(author, entry.ActorId);
        Assert.Equal(pending.ConfirmedAt, entry.OccurredAt);
        Assert.Equal("Исходная причина", entry.After);
        Assert.NotNull(await db.InvoiceDuplicateConfirmations.Where(p => p.EventId == pending.EventId)
            .Select(p => p.JournaledAt).SingleAsync());
    }

    private sealed class FailingDelivery(IModuleDeferredActivityLog inner, bool afterAppend) : IModuleDeferredActivityLog
    {
        public async Task RecordDeferredAsync(ModuleActivityAction action, ModuleDeferredActivity saved, CancellationToken ct)
        {
            if (afterAppend) await inner.RecordDeferredAsync(action, saved, ct);
            throw new InvalidOperationException("Проверка повторной доставки");
        }
    }

    private static async Task WaitForLockAsync(CostsDbContext db, string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND mode = {mode}
                AND classid::bigint = {LockKey >> 32} AND objid::bigint = {LockKey & uint.MaxValue}) AS "Value"
            """).SingleAsync(deadline.Token))
            await Task.Delay(20, deadline.Token);
    }
}
