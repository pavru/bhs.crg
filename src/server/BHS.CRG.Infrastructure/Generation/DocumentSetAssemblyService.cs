using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Собирает весь комплект в один PDF: догенерирует недостающие документы (существующим
/// <see cref="GenerateDocumentCommand"/>, не параллельным путём) и склеивает готовые PDF по порядку
/// (<see cref="DocumentInstance.SortOrder"/>, внутри документа — по порядку шаблонов). Опирается на
/// инвариант <see cref="DocumentInstance.ResetToDraft"/> (правка → Draft + удаление файлов): статус
/// Generated ⇒ файлы актуальны, переиспользуем; Draft/Failed ⇒ (пере)генерируем.
/// <para>Сбой любого документа прерывает сборку (по решению пользователя — «всё или ничего»): собирается
/// отчёт обо ВСЕХ не готовых документах, комплект не выпускается. Запускается фоновой задачей
/// (<see cref="Domain.Jobs.JobKind.AssembleDocumentSet"/>), итог — в уведомления.</para>
/// </summary>
public class DocumentSetAssemblyService(
    IMediator mediator,
    IRepository<DocumentSet> setRepo,
    IDomainObjectRepository objRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<DocumentSetOutput> outputRepo,
    IBlobStorage blob,
    INotificationService notifications)
{
    /// <summary>
    /// Собирает комплект. <paramref name="subsetIds"/> — необязательное подмножество документов (иначе весь
    /// комплект). <paramref name="reportProgress"/>(готово, всего) — честный прогресс по документам.
    /// </summary>
    public async Task AssembleAsync(Guid setId, IReadOnlyList<Guid>? subsetIds, Guid userId,
        CancellationToken ct, Func<int, int, Task>? reportProgress = null)
    {
        var set = await setRepo.GetByIdAsync(setId, ct) ?? throw new KeyNotFoundException("Комплект не найден");

        var included = (await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct))
            .OrderBy(i => i.SortOrder).ToList();
        if (subsetIds is { Count: > 0 })
        {
            var wanted = subsetIds.ToHashSet();
            included = included.Where(i => wanted.Contains(i.Id)).ToList();
        }
        if (included.Count == 0)
            throw new InvalidOperationException("В комплекте нет документов для сборки.");

        var docTypes = (await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        string Name(DomainObject i) =>
            i.DisplayName ?? (docTypes.TryGetValue(i.CompositeTypeId, out var t) ? t.Name : "Документ");

        // Проход 1 — гарантируем, что каждый документ сгенерирован. Собираем ВСЕ сбои, затем прерываем.
        var failures = new List<string>();
        var done = 0;
        foreach (var inst in included)
        {
            ct.ThrowIfCancellationRequested();
            var hasPdf = inst.Status == DocumentStatus.Generated
                && inst.GeneratedFiles.Any(f => f.Format == OutputFormat.Pdf);
            if (!hasPdf)
            {
                try
                {
                    await mediator.Send(new GenerateDocumentCommand(inst.Id, OutputFormat.Pdf,
                        GeneratedBy: "Сборка комплекта", UserId: userId), ct);
                }
                catch (Exception ex)
                {
                    failures.Add($"«{Name(inst)}» — {Summarize(ex)}");
                }
            }
            done++;
            if (reportProgress is not null) await reportProgress(done, included.Count);
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Сборка прервана — не готовы документы:\n" + string.Join("\n", failures.Select(f => " • " + f)));

        // Проход 2 — перечитываем документы комплекта (свежие файлы) и склеиваем PDF по порядку.
        set = await setRepo.GetByIdAsync(setId, ct) ?? throw new KeyNotFoundException("Комплект не найден");
        var includedIds = included.Select(i => i.Id).ToHashSet();
        var ordered = (await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct))
            .Where(i => includedIds.Contains(i.Id)).OrderBy(i => i.SortOrder);

        var pdfBytes = new List<byte[]>();
        foreach (var inst in ordered)
        {
            foreach (var file in OrderPdfFiles(inst))
                pdfBytes.Add(await DownloadAsync(file.BlobPath, ct));
        }
        if (pdfBytes.Count == 0)
            throw new InvalidOperationException("Не удалось собрать: у выбранных документов нет PDF.");

        var merged = PdfMerger.Merge(pdfBytes);

        // Загружаем и заменяем единственный вывод комплекта (старый blob удаляем).
        using var ms = new MemoryStream(merged);
        var blobPath = await blob.UploadAsync($"{set.Name}.pdf", ms, "application/pdf", ct);

        var existing = (await outputRepo.FindAsync(o => o.SetId == setId, ct)).FirstOrDefault();
        if (existing is not null)
        {
            var oldPath = existing.BlobPath;
            outputRepo.Remove(existing);
            try { await blob.DeleteAsync(oldPath, ct); } catch { /* best effort */ }
        }
        await outputRepo.AddAsync(DocumentSetOutput.Create(setId, blobPath, OutputFormat.Pdf), ct);
        await outputRepo.SaveChangesAsync(ct);

        await notifications.PublishAsync(NotificationSeverity.Info, "Комплект собран",
            $"«{set.Name}»: {pdfBytes.Count} PDF из {included.Count} документов.", "Сборка комплекта", userId: userId);
    }

    /// <summary>PDF-файлы документа в порядке шаблонов (TemplateIds); неупорядоченные — в конец.</summary>
    private static IEnumerable<GeneratedFile> OrderPdfFiles(DomainObject inst)
    {
        var pdfs = inst.GeneratedFiles.Where(f => f.Format == OutputFormat.Pdf).ToList();
        var order = ParseTemplateIds(inst.TemplateIds);
        if (order.Count == 0) return pdfs;
        int Rank(GeneratedFile f)
        {
            var idx = f.TemplateId is null ? -1 : order.IndexOf(f.TemplateId.Value);
            return idx < 0 ? int.MaxValue : idx;
        }
        return pdfs.OrderBy(Rank);
    }

    private static List<Guid> ParseTemplateIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch { return []; }
    }

    private async Task<byte[]> DownloadAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    // Короткая причина сбоя документа для отчёта. Первая строка сообщения (диагностику пользователь
    // увидит полностью, открыв документ в редакторе).
    private static string Summarize(Exception ex)
    {
        var msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) return ex.GetType().Name;
        var nl = msg.IndexOf('\n');
        return nl > 0 ? msg[..nl].Trim() : msg.Trim();
    }
}
