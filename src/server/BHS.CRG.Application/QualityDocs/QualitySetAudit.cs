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
public record QualityAuditReport(
    Guid SetId,
    int Documents,
    int Failed,
    int MaterialsWithoutDoc,
    int ImplausibleDocs,
    IReadOnlyList<QualityAuditRow> Rows);

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
/// </summary>
public record QualitySetAuditQuery(Guid SetId) : IRequest<QualityAuditReport>;

public class QualitySetAuditHandler(IDomainObjectRepository objects, IMediator mediator)
    : IRequestHandler<QualitySetAuditQuery, QualityAuditReport>
{
    public async Task<QualityAuditReport> Handle(QualitySetAuditQuery q, CancellationToken ct)
    {
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
            catch (Exception) { failed++; continue; }

            foreach (var d in diagnostics)
            {
                if (d.Code != QualityLinkScanner.Code && d.Code != QualityLinkScanner.ImplausibleCode) continue;
                rows.Add(new QualityAuditRow(doc.Id, doc.DisplayName ?? "", d.Code, d.Path, d.Message));
            }
        }

        return new QualityAuditReport(
            q.SetId,
            documents.Count,
            failed,
            rows.Count(r => r.Code == QualityLinkScanner.Code),
            rows.Count(r => r.Code == QualityLinkScanner.ImplausibleCode),
            rows);
    }
}
