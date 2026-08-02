using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
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
public record QualityAuditReport(
    Guid SetId,
    int Documents,
    int Failed,
    int MaterialsWithoutDoc,
    int ImplausibleDocs,
    IReadOnlyList<QualityAuditRow> Rows,
    bool Truncated = false);

/// <summary>
/// Сверка «реестр материалов ↔ карта документов качества» по всему комплекту (issue #589).
///
/// Ровно эту проверку внешний агент делал руками: выгружал обе стороны целиком и сличал. Стороны
/// большие (151 строка реестра против 113 связей), а ответ — десяток строк, поэтому считать должен
/// сервер: выгрузка сырья ради вывода, который умещается в экран, — это и есть основная статья
/// расхода контекста.
///
/// Считается ТЕМ ЖЕ путём, что и проверка отдельного документа: <see cref="ValidateInstanceResolutionQuery"/>
/// прогоняет полный резолв и отдаёт диагностики, отсюда берутся только качественные. Своего прохода
/// по данным здесь нет намеренно — пайплайн резолва и без того размножен по нескольким местам, и
/// шестая копия неизбежно разошлась бы с остальными.
///
/// Цена решения: резолв документа читает наборы данных (блоб + разбор файла), и на большом комплекте
/// прогон занимает минуты внутри одного HTTP-запроса. Длинные прогоны переезжают в подсистему Job
/// отдельной задачей — issue #628; здесь ограничен только объём ответа.
/// </summary>
/// <param name="Limit">Сколько строк вернуть. Счётчики считаются по всем находкам.</param>
public record QualitySetAuditQuery(Guid SetId, int Limit = QualitySetAuditHandler.DefaultLimit)
    : IRequest<QualityAuditReport>;

public class QualitySetAuditHandler(
    IDomainObjectRepository objects,
    IRepository<Domain.Documents.DocumentSet> sets,
    IMediator mediator
) : IRequestHandler<QualitySetAuditQuery, QualityAuditReport>
{
    /// <summary>
    /// Сколько находок показывать. Смысл сверки — короткий ответ вместо двух выгруженных таблиц, а
    /// на живых данных находок бывает под сотню (151 материал, 68 неверных связок): без предела
    /// ответ вырос бы больше того сырья, которое он заменяет. Полные числа остаются в счётчиках.
    /// </summary>
    public const int DefaultLimit = 100;

    public async Task<QualityAuditReport> Handle(QualitySetAuditQuery q, CancellationToken ct)
    {
        // Несуществующий комплект — 404, а не «проблем нет»: пустой отчёт на опечатку в
        // идентификаторе читается как чистая совесть, и это ровно тот молчаливый ноль, из-за
        // которого 68 неверных связок жили незамеченными.
        _ = await sets.GetByIdAsync(q.SetId, ct)
            ?? throw new KeyNotFoundException($"DocumentSet {q.SetId} not found");

        var documents = await objects.GetSetDocumentsAsync(q.SetId, tracked: false, ct);
        var rows = new List<QualityAuditRow>();
        var failed = 0;

        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ResolutionDiagnostic> diagnostics;
            // Один нечитаемый документ не должен отменять сверку остальных: комплект собирают
            // месяцами, и сломанный набор в одном документе — обычное состояние работы.
            try { diagnostics = await mediator.Send(new ValidateInstanceResolutionQuery(doc.Id), ct); }
            // Отмену наружу: клиент ушёл — считать нечего и незачем, а записанная в Failed отмена
            // выглядела бы как «документ сломан» и отправила бы человека искать несуществующий дефект.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; continue; }

            foreach (var d in diagnostics)
            {
                if (d.Code != QualityLinkScanner.Code && d.Code != QualityLinkScanner.ImplausibleCode) continue;
                rows.Add(new QualityAuditRow(doc.Id, doc.DisplayName ?? "", d.Code, d.Path, d.Message));
            }
        }

        var limit = q.Limit > 0 ? q.Limit : DefaultLimit;
        return new QualityAuditReport(
            q.SetId,
            documents.Count,
            failed,
            rows.Count(r => r.Code == QualityLinkScanner.Code),
            rows.Count(r => r.Code == QualityLinkScanner.ImplausibleCode),
            [.. rows.Take(limit)],
            Truncated: rows.Count > limit);
    }
}
