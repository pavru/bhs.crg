using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Файл чтения отчёта монтажника (<c>IIsolatedRecord</c>, <c>work-07</c>; change <c>work-13-report-model</c>): здесь и
/// только здесь отчёт и его строки читаются напрямую (перепись — <c>IsolatedReadInventoryTests</c>).
///
/// <para><b>Сейчас — только вопросы пути записи «занято ли»</b>: ответ не несёт полей чужого отчёта, только факт
/// занятости, а у ключа — идентификатор отчёта ТОГО ЖЕ сотрудника. Чтение отчётов с охватом
/// (<c>ModuleReader</c>, «свои» и «своих строек») приходит сюда же с адресами, во втором PR change.</para>
/// </summary>
public sealed class ReportReads(WorkDbContext db)
{
    /// <summary>
    /// Отчёт сотрудника с той же четвёркой «стройка, дата, смена» (ТЗ WORK-4, MOB-35), кроме
    /// <paramref name="except" />; <c>null</c> — четвёрка свободна. Вопрос задаёт путь записи ДО позиций перечня:
    /// уникальный индекс <c>ux_reports_key</c> остаётся последним рубежом для двух запросов разом.
    /// </summary>
    public Task<Guid?> KeyHolderAsync(
        Guid employeeId, Guid constructionId, DateOnly date, ReportShift shift, Guid except, CancellationToken ct) =>
        db.Set<Report>().AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.ConstructionId == constructionId && r.Date == date
                        && r.Shift == shift && r.Id != except)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Идентификаторы строк, которые уже стоят в ДРУГИХ отчётах. Идентификатор строки — от клиента и ключ таблицы:
    /// занятый не вставить, и без вопроса отказ пришёл бы нарушением ключа при сохранении.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LinesOfOtherReportsAsync(
        Guid reportId, IReadOnlyCollection<Guid> lineIds, CancellationToken ct) =>
        lineIds.Count == 0
            ? []
            : await db.Set<ReportLine>().AsNoTracking()
                .Where(l => lineIds.Contains(l.Id) && l.ReportId != reportId)
                .Select(l => l.Id)
                .ToListAsync(ct);
}
