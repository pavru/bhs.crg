using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Удаление счёта (issue #1282; решения владельца продукта 11.10.2026).
///
/// <para><b>Удаляется только черновик без оплаты — насовсем, вместе с файлом.</b> Удаление — для счёта,
/// заведённого по ошибке, и для дубля. Настоящий счёт, по которому не платим, не удаляют, а отклоняют
/// (issue #1284): он остаётся в реестре. Разобранный и отклонённый сначала возвращают в черновик,
/// у оплаченного — отменяют оплату; отказ называет именно этот выход.</para>
///
/// <para>Условия «не заперт закрытым периодом» здесь нет, и это не пропуск: замок считается только у
/// оплаченного счёта, а отменить оплату счёта из закрытого периода связка записи не даёт.</para>
///
/// <para>⚠️ <b>Удаление — отслеживаемое <c>Remove</c> под связкой записи</b>, не <c>ExecuteDelete</c>:
/// двух писателей одного счёта разводит версия строки при сохранении, а запись мимо отслеживания связка
/// не видит вовсе (<c>InvoiceWritePathTests</c>). Строки, доли разноски и запись распознавания уходят
/// каскадом базы.</para>
///
/// <para>⚠️ <b>Порядок: связка → журнал → файл.</b> Файл удаляется последним и без токена отмены: счёт
/// уже удалён, и оборванный запрос не вправе оставить файл. Отказ хранилища — НЕ отказ удаления: счёта
/// нет, а осиротевший файл уберёт обычная уборка. Замену скана за образец не брать — там отказ
/// хранилища отвечает 500 при уже сохранённом счёте.</para>
/// </summary>
public static class InvoiceDeletion
{
    /// <summary>Что осталось от счёта после удаления: чем назвать его в журнале и какой файл убрать.</summary>
    private sealed record Gone(string Label, Guid? SupplierId, string? ScanBlobPath, string? ScanFileName);

    /// <summary>
    /// Удалить счёт. Ответ — 204 без тела: читать после удаления нечего. Повтор отвечает 404 — клиент
    /// читает его как «уже удалён».
    /// </summary>
    public static async Task<NoContent> DeleteAsync(
        Guid id, CostsDbContext db, InvoiceDesk desk, IModuleCatalog catalog, IModuleBlobs blobs,
        IModuleActivityLog log, ILoggerFactory logs, CancellationToken ct)
    {
        Gone gone;
        try
        {
            gone = await desk.WriteAsync(id, async write =>
            {
                var invoice = write.Invoice;
                EnsureDeletable(invoice);

                var gone = new Gone(InvoiceEndpoints.Label(invoice), invoice.SupplierId, invoice.ScanBlobPath, invoice.ScanFileName);
                db.Invoices.Remove(invoice);
                await db.SaveChangesAsync(ct);
                return gone;
            }, ct);
        }
        catch (ConflictException lost) when (lost.InnerException is DbUpdateConcurrencyException)
        {
            // Двое удаляли разом (ревью PR #1298): второй прошёл сверку версии и не нашёл строки. Ему
            // обязано ответить то же «нет такого», что и повтору, — «счёт изменили, действие не
            // выполнено» про удалённый счёт было бы неправдой. Счёт на месте — отказ остаётся отказом.
            db.ChangeTracker.Clear();
            if (await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == id, CancellationToken.None)) throw;
            throw new NotFoundException("Счёт не найден.");
        }

        // ⚠️ После связки — без токена отмены и без права на отказ (ревью PR #1298): счёт удалён
        // насовсем, и оборванный запрос не вправе оставить это без записи в журнале, а отказ журнала —
        // ответить «сбой» про удаление, которое состоялось. Так же ведёт себя заведение счёта из файла.
        try
        {
            await log.RecordAsync(InvoiceActions.Deleted, id.ToString(), gone.Label,
                before: await DescribedAsync(catalog, gone), after: "удалён насовсем", ct: CancellationToken.None);
        }
        catch (Exception lost)
        {
            logs.CreateLogger(typeof(InvoiceDeletion)).LogError(lost,
                "{Label} ({InvoiceId}) удалён, но запись об этом в журнал действий не легла.", gone.Label, id);
        }

        await RemoveFileAsync(blobs, gone, id, logs);
        return TypedResults.NoContent();
    }

    private static void EnsureDeletable(Invoice invoice)
    {
        var label = InvoiceEndpoints.Label(invoice);

        // Оплата — первой: у оплаченного разобранного счёта выход начинается с неё.
        if (invoice.Payment == InvoicePaymentState.Paid)
            throw new ConflictException(
                $"{label} оплачен — удалить его нельзя: его сумма вошла в затраты. Сначала отмените оплату; " +
                "удаляется только черновик без оплаты.");

        if (invoice.State != InvoiceState.Draft)
            throw new ConflictException(
                $"{label} — {InvoiceRequisites.Label(invoice.State).ToLowerInvariant()}, а удаляется только " +
                "черновик. Верните счёт в черновик — и удалите. Настоящий счёт, по которому не платим, не " +
                "удаляют, а отклоняют: он остаётся в реестре.");
    }

    /// <summary>
    /// Чем счёт опознать в журнале, когда самого счёта уже нет: поставщик и имя файла. У счёта без номера
    /// имя файла — единственное, что отличает одну запись «счёт без номера удалён» от другой.
    ///
    /// <para>⚠️ <b>Суммы здесь нет</b> (issue #1190, <c>CostsJournalMoneyTests</c>): журнал ядра читают
    /// без права на счета. Поставщик читается ПОСЛЕ удаления — он запись справочника ядра и от счёта не
    /// зависит; справочник, который не ответил, оставляет запись без поставщика, а не без записи.</para>
    /// </summary>
    private static async Task<string?> DescribedAsync(IModuleCatalog catalog, Gone gone)
    {
        string? supplier = null;
        if (gone.SupplierId is { } id)
        {
            try
            {
                supplier = (await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, [id], CancellationToken.None))
                    ?.FirstOrDefault(o => o.Id == id)?.DisplayName;
            }
            catch (Exception)
            {
                // Счёт удалён: отказ справочника не вправе отнять у журнала саму запись об этом.
            }
        }

        var parts = new[]
        {
            supplier is { Length: > 0 } ? $"поставщик: {supplier}" : null,
            gone.ScanFileName is { Length: > 0 } file ? $"файл: {file}" : null,
        }.OfType<string>().ToList();
        return parts.Count > 0 ? string.Join("; ", parts) : "черновик без поставщика и файла";
    }

    private static async Task RemoveFileAsync(IModuleBlobs blobs, Gone gone, Guid id, ILoggerFactory logs)
    {
        if (gone.ScanBlobPath is not { } path) return;

        try
        {
            // Читаемый образ офисного файла и запись реестра блобов снимает сам вызов хранилища.
            await blobs.DeleteAsync(path, CancellationToken.None);
        }
        catch (Exception lost)
        {
            logs.CreateLogger(typeof(InvoiceDeletion)).LogWarning(lost,
                "Счёт {InvoiceId} удалён, но его файл {Path} убрать не удалось — его подберёт уборка осиротевших.",
                id, path);
        }
    }
}
