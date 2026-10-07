using System.Text.Json;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Что известно о распознавании скана счёта — для формы (ТЗ COST-8: состояния различимы).
/// </summary>
/// <param name="State"><c>none</c> — не запускалось; <c>running</c> — идёт, форму можно заполнять
/// руками; <c>done</c> — прочитано; <c>failed</c> — не удалось, причина в <paramref name="Reason" />
/// и <paramref name="Error" />.</param>
/// <param name="Reason"><c>NotConfigured</c> — движок не настроен; <c>Unavailable</c> — не справился;
/// <c>NoAnswer</c> — документ не прочитан; <c>Interrupted</c> — задача прервана; <c>Refused</c> —
/// прочитанное не записалось.</param>
/// <param name="Values">Что прочитано в шапке, как есть: «ключ профиля → текст».</param>
/// <param name="Offers">Прочитано, но в поле не записано: поле было занято или значение не разобрать.
/// «Ключ реквизита → текст из скана».</param>
/// <param name="Lines">Распознанные строки, которые в счёт не легли: у него уже были свои.</param>
/// <param name="CanStart">Можно ли запустить сейчас — словами сервера, чтобы кнопка и отказ не
/// расходились; причина — в <paramref name="WhyNot" />.</param>
public sealed record InvoiceRecognitionView(
    string State, string? Reason, string? Error, string? Engine, string? Progress,
    JsonElement? Values, JsonElement? Offers, JsonElement? Lines, IReadOnlyList<string> Notes,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, bool CanStart, string? WhyNot);

/// <summary>
/// Распознавание скана счёта: постановка и состояние (ТЗ COST-8, задача B1b, issue #1077). Само чтение
/// и раскладка прочитанного по черновику — в <see cref="InvoiceScanReading" />.
/// </summary>
public sealed class InvoiceScanRecognition(
    CostsDbContext db, IModuleRecognition recognition, IModuleJobs jobs)
{
    public const string Operation = "costs.invoice.recognize";

    /// <summary>Что движки читают наверняка. Остальное приложить можно, распознать — нет.</summary>
    private static readonly HashSet<string> Readable =
        new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/png", "image/jpeg" };

    internal static bool IsReadable(string? mimeType) => mimeType is not null && Readable.Contains(mimeType);

    /// <summary>
    /// Почему распознать нельзя; <c>null</c> — можно. Одно место на кнопку, на адрес и на обработчик:
    /// условие проверяется при постановке и СНОВА перед записью — между ними счёт могли разобрать.
    /// </summary>
    internal static string? WhyNot(Invoice invoice) =>
        invoice.ScanBlobPath is null ? "к счёту не приложен скан"
        : !IsReadable(invoice.ScanMimeType) ? "распознаются PDF, PNG и JPEG, а приложен файл другого вида"
        : invoice.State != InvoiceState.Draft ? "счёт уже не черновик: распознанное меняет поля и строки"
        : invoice.Payment == InvoicePaymentState.Paid ? "счёт оплачен"
        : null;

    /// <summary>Идёт ли распознавание: запись ждёт исхода, а её задача жива.</summary>
    public async Task<bool> IsRunningAsync(Guid invoiceId, CancellationToken ct)
    {
        var stored = await db.InvoiceRecognitions.AsNoTracking().FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, ct);
        return stored is not null && await AliveAsync(stored, ct) is not null;
    }

    public async Task<InvoiceRecognitionView> ViewAsync(Invoice invoice, CancellationToken ct)
    {
        var stored = await db.InvoiceRecognitions.AsNoTracking().FirstOrDefaultAsync(r => r.InvoiceId == invoice.Id, ct);
        var whyNot = WhyNot(invoice);

        if (stored is null)
            return new("none", null, null, null, null, null, null, null, [], null, null, whyNot is null, whyNot);

        if (stored.Outcome == InvoiceRecognitionOutcome.Pending)
        {
            if (await AliveAsync(stored, ct) is { } job)
                return new("running", null, null, null, job.Progress, null, null, null, [], stored.StartedAt, null,
                    false, "распознавание уже идёт");

            // Запись ждёт исхода, а ждать нечего: задача упала мимо нас (перезапуск сервера, чужая
            // ошибка) или до очереди не дошла. Это отказ, и назвать его «идёт» было бы ложью навсегда.
            var died = stored.JobId is { } id ? await jobs.GetAsync(id, ct) : null;
            return new("failed", InvoiceRecognition.Interrupted,
                died?.Error ?? "Распознавание прервано и исхода не оставило. Запустите его ещё раз.",
                null, null, null, null, null, [], stored.StartedAt, null, whyNot is null, whyNot);
        }

        return new(
            stored.Outcome == InvoiceRecognitionOutcome.Done ? "done" : "failed",
            stored.Reason, stored.Error, stored.Engine, null,
            stored.Values?.RootElement, stored.Offers?.RootElement, stored.Lines?.RootElement, stored.Notes,
            stored.StartedAt, stored.FinishedAt, whyNot is null, whyNot);
    }

    private async Task<ModuleJobState?> AliveAsync(InvoiceRecognition stored, CancellationToken ct)
    {
        if (stored.Outcome != InvoiceRecognitionOutcome.Pending || stored.JobId is not { } id) return null;
        return await jobs.GetAsync(id, ct) is { Status: ModuleJobStatus.Queued or ModuleJobStatus.Running } job ? job : null;
    }

    /// <summary>
    /// Поставить распознавание в фон. Счёт при этом НЕ меняется — пишется только запись о
    /// распознавании, и версия счёта остаётся прежней.
    ///
    /// <para>«Распознавать некому» — не отказ запроса, а исход: черновик со сканом остаётся, человек
    /// заполняет его руками, а форма так и пишет — «распознавание не настроено». Остальное, что
    /// запуску мешает (нет скана, счёт не черновик, уже идёт), — отказ с причиной.</para>
    /// </summary>
    public async Task StartAsync(Invoice invoice, CancellationToken ct)
    {
        if (WhyNot(invoice) is { } whyNot)
            throw new ConflictException($"{InvoiceEndpoints.Label(invoice)}: распознать нельзя — {whyNot}.");

        var stored = await db.InvoiceRecognitions.FirstOrDefaultAsync(r => r.InvoiceId == invoice.Id, ct);
        if (stored is not null && await AliveAsync(stored, ct) is not null)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: скан уже распознаётся. Дождитесь исхода — второй запуск " +
                "прочитал бы тот же файл и ничего не добавил.");

        if (stored is null) db.InvoiceRecognitions.Add(stored = InvoiceRecognition.Start(invoice.Id, invoice.ScanBlobPath!));
        else stored.Restart(invoice.ScanBlobPath!);

        try
        {
            await recognition.EnsureReadyAsync(CostsRecognitionProfiles.InvoiceCode, ct);
        }
        catch (RecognitionRefusedException refused)
        {
            stored.Fail(refused.Reason.ToString(), refused.Message);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Запись — ДО постановки: обработчик находит её по счёту и может стартовать раньше, чем мы
        // сохраним идентификатор задачи. Обратный порядок оставил бы задачу без записи об исходе.
        await db.SaveChangesAsync(ct);
        try
        {
            stored.Queued(await jobs.EnqueueAsync(
                Operation, invoice.Id, $"Распознавание скана: {InvoiceEndpoints.Label(invoice)}", invoice.ScanBlobPath, ct));
        }
        catch (DomainException refusal)
        {
            stored.Fail(InvoiceRecognition.Interrupted, refusal.Message);
            await db.SaveChangesAsync(ct);
            throw;
        }
        await db.SaveChangesAsync(ct);
    }
}
