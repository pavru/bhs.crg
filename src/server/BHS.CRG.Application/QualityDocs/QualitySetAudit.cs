using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>Одна находка сверки: что за материал, в каком документе и что с ним не так.</summary>
/// <param name="Code">Вид находки — <c>material-no-quality-doc</c> либо <c>quality-doc-implausible</c>.</param>
/// <param name="Path">Путь в контексте документа (поле-массив, индекс строки) — адрес, а не пересказ.</param>
public record QualityAuditRow(Guid InstanceId, string InstanceName, string Code, string Path, string Message);

/// <param name="Documents">Сколько документов комплекта проверено.</param>
/// <param name="Failed">Документы, которые проверить НЕ удалось (тип удалён, набор не читается).
/// Молчание об этом означало бы «всё хорошо» там, где просто не смотрели.</param>
/// <param name="Truncated">Находок больше, чем строк в <paramref name="Rows"/>. Счётчики при этом
/// полные: усечён показ, а не подсчёт — иначе «10 из 10» читалось бы как «всё в порядке».</param>
/// <param name="CompletedAt">Когда прогон завершился. Заполнено у СОХРАНЁННОГО отчёта: он верен на
/// эту дату и с правкой данных устаревает молча, поэтому «находок нет» без даты читалось бы как
/// утверждение о сегодняшнем состоянии комплекта.</param>
public record QualityAuditReport(
    Guid SetId,
    int Documents,
    int Failed,
    int MaterialsWithoutDoc,
    int ImplausibleDocs,
    IReadOnlyList<QualityAuditRow> Rows,
    bool Truncated = false,
    DateTimeOffset? CompletedAt = null);

/// <summary>
/// Сверка «реестр материалов ↔ карта документов качества» по всему комплекту (issue #589).
///
/// Ровно эту проверку внешний агент делал руками: выгружал обе стороны целиком и сличал. Стороны
/// большие (151 строка реестра против 113 связей), а ответ — десяток строк, поэтому считать должен
/// сервер: выгрузка сырья ради вывода, который умещается в экран, — это и есть основная статья
/// расхода контекста.
///
/// Считается ТЕМ ЖЕ путём, что и проверка отдельного документа: <see cref="IInstanceResolutionValidator"/>
/// прогоняет полный резолв и отдаёт диагностики, отсюда берутся только качественные. Своего прохода
/// по данным здесь нет намеренно — пайплайн резолва и без того размножен по нескольким местам, и
/// шестая копия неизбежно разошлась бы с остальными.
///
/// Прогон ДОЛГИЙ: резолв каждого документа читает наборы данных (блоб + разбор файла), и на комплекте
/// в полсотни документов это минуты. Поэтому синхронного вызова у сверки больше нет — она ставится
/// фоновой задачей (issue #628), а её итог сохраняется одной строкой на комплект и читается отдельно.
/// </summary>
public interface IQualitySetAuditRunner
{
    /// <summary>Прогнать сверку и вернуть отчёт, ничего не сохраняя.</summary>
    /// <param name="onProgress">Обратный вызов «проверено из скольких» для индикатора задач.</param>
    Task<QualityAuditReport> RunAsync(Guid setId, int limit, Func<int, int, Task>? onProgress, CancellationToken ct);

    /// <summary>Прогнать сверку, заменить сохранённый отчёт комплекта и сообщить итог в колокольчик.</summary>
    Task<QualityAuditReport> RunAndStoreAsync(Guid setId, Guid userId, Func<int, int, Task>? onProgress, CancellationToken ct);
}

public class QualitySetAuditRunner(
    IDomainObjectRepository objects,
    IRepository<DocumentSet> sets,
    IRepository<QualityAuditRun> runs,
    IInstanceResolutionValidator validator,
    INotificationService notifications
) : IQualitySetAuditRunner
{
    /// <summary>
    /// Сколько находок сохранять и показывать. Смысл сверки — короткий ответ вместо двух выгруженных
    /// таблиц, а на живых данных находок бывает под сотню (151 материал, 68 неверных связок): без
    /// предела ответ вырос бы больше того сырья, которое он заменяет. Полные числа остаются в счётчиках.
    /// </summary>
    public const int DefaultLimit = 100;

    public async Task<QualityAuditReport> RunAsync(Guid setId, int limit, Func<int, int, Task>? onProgress, CancellationToken ct)
    {
        // Несуществующий комплект — отказ, а не «проблем нет»: пустой отчёт на опечатку в
        // идентификаторе читается как чистая совесть, и это ровно тот молчаливый ноль, из-за
        // которого 68 неверных связок жили незамеченными.
        _ = await sets.GetByIdAsync(setId, ct)
            ?? throw new KeyNotFoundException($"DocumentSet {setId} not found");

        var documents = await objects.GetSetDocumentsAsync(setId, tracked: false, ct);
        var rows = new List<QualityAuditRow>();
        var failed = 0;

        // Справочники схемы — ОДИН раз на прогон. Раньше проверка читала все типы документов и все
        // примитивные типы на каждый документ: полсотни одинаковых запросов подряд (issue #628).
        var catalog = await validator.LoadCatalogAsync(ct);

        var done = 0;
        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ResolutionDiagnostic> diagnostics;
            // Один нечитаемый документ не должен отменять сверку остальных: комплект собирают
            // месяцами, и сломанный набор в одном документе — обычное состояние работы.
            try { diagnostics = await validator.ValidateAsync(doc.Id, catalog, ct); }
            // Отмену наружу: клиент ушёл — считать нечего и незачем, а записанная в Failed отмена
            // выглядела бы как «документ сломан» и отправила бы человека искать несуществующий дефект.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; diagnostics = []; }

            // Прогресс считаем и по неудавшимся документам: «43 из 50» должно означать «сорок три
            // просмотрено», иначе счётчик замирал бы ровно на сломанном месте.
            done++;
            if (onProgress is not null) await onProgress(done, documents.Count);

            foreach (var d in diagnostics)
            {
                if (d.Code != QualityLinkScanner.Code && d.Code != QualityLinkScanner.ImplausibleCode) continue;
                rows.Add(new QualityAuditRow(doc.Id, doc.DisplayName ?? "", d.Code, d.Path, d.Message));
            }
        }

        var effectiveLimit = limit > 0 ? limit : DefaultLimit;
        return new QualityAuditReport(
            setId,
            documents.Count,
            failed,
            rows.Count(r => r.Code == QualityLinkScanner.Code),
            rows.Count(r => r.Code == QualityLinkScanner.ImplausibleCode),
            [.. rows.Take(effectiveLimit)],
            Truncated: rows.Count > effectiveLimit);
    }

    public async Task<QualityAuditReport> RunAndStoreAsync(Guid setId, Guid userId, Func<int, int, Task>? onProgress, CancellationToken ct)
    {
        var report = await RunAsync(setId, DefaultLimit, onProgress, ct);
        var total = report.MaterialsWithoutDoc + report.ImplausibleDocs;

        // Замена, а не накопление: отчёт один на комплект (см. QualityAuditRun). Существующий читается
        // без отслеживания, поэтому удаляем и добавляем — тем же приёмом, что и собранный файл комплекта.
        var existing = (await runs.FindAsync(r => r.SetId == setId, ct)).FirstOrDefault();
        if (existing is not null) runs.Remove(existing);
        await runs.AddAsync(QualityAuditRun.Create(setId, report.Documents, report.Failed,
            report.MaterialsWithoutDoc, report.ImplausibleDocs, total,
            JsonSerializer.Serialize(report.Rows)), ct);
        await runs.SaveChangesAsync(ct);

        var set = await sets.GetByIdAsync(setId, ct);
        // Итог в колокольчик: задача уходит из индикатора молча, и без этого «сверка прошла» было бы
        // видно только тому, кто в этот момент смотрел на индикатор.
        var summary = total == 0
            ? $"Проверено документов: {report.Documents}. Замечаний по документам качества нет."
            : $"Проверено документов: {report.Documents}. Материалов без документа качества: "
              + $"{report.MaterialsWithoutDoc}, сомнительных связок: {report.ImplausibleDocs}.";
        if (report.Failed > 0) summary += $" Не удалось проверить: {report.Failed}.";
        await notifications.PublishAsync(
            total == 0 ? NotificationSeverity.Info : NotificationSeverity.Warning,
            $"Сверка качества: «{set?.Name ?? "комплект"}»", summary, "Документы качества", userId: userId);

        return report;
    }
}

/// <summary>
/// Сохранённый отчёт последней сверки комплекта. Отдельный запрос, потому что прогон фоновый:
/// запускает его одна операция, а спрашивает итог другая — в том числе после перезагрузки страницы.
/// <c>null</c> — сверку по комплекту ещё не запускали; пустой отчёт вместо этого утверждал бы, что
/// проверено и чисто.
/// </summary>
public record GetQualityAuditQuery(Guid SetId) : IRequest<QualityAuditReport?>;

public class GetQualityAuditHandler(IRepository<QualityAuditRun> runs)
    : IRequestHandler<GetQualityAuditQuery, QualityAuditReport?>
{
    public async Task<QualityAuditReport?> Handle(GetQualityAuditQuery q, CancellationToken ct)
    {
        var run = (await runs.FindAsync(r => r.SetId == q.SetId, ct)).FirstOrDefault();
        if (run is null) return null;

        var rows = JsonSerializer.Deserialize<List<QualityAuditRow>>(run.RowsJson) ?? [];
        return new QualityAuditReport(run.SetId, run.Documents, run.Failed, run.MaterialsWithoutDoc,
            run.ImplausibleDocs, rows, Truncated: run.TotalFindings > rows.Count, CompletedAt: run.CompletedAt);
    }
}
