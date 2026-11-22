// Синтетические отчёты очереди для Work-21. Справочник и назначения уже подготовил seed-employees.
import { randomUUID } from 'node:crypto';

export const REVIEW_REPORTS = [
  { id: '187f44ed-6fc5-4234-9b24-e270df33a052', name: 'Крылов Максим Игоревич (посев)', number: 'E2E-21', shift: 'day' },
  { id: '2e874522-0737-4629-85ed-82904fd8a092', name: 'Сафонова Ирина Олеговна (посев)', number: 'E2E-22', shift: 'night' },
];

export async function seedReview({ api, constructionId }) {
  const { construction } = await api('GET', `/work/assignments?constructionId=${constructionId}`);
  if (!construction.today) throw new Error('У стройки посева нет текущей даты для отчётов Work-21.');
  const employees = await api('GET', '/employees');
  for (const report of REVIEW_REPORTS) {
    const employee = employees.find(item => item.displayName === report.name)
      ?? await api('POST', '/employees', { displayName: report.name, data: JSON.stringify({ ТабельныйНомер: report.number }) });
    const path = `/work/entry/reports/${report.id}`;
    const body = { id: report.id, employeeId: employee.id, constructionId, date: construction.today, shift: report.shift, hours: 8, lines: [] };
    let current = await api('GET', path).catch(error => {
      if (!/ → 404 /.test(error.message)) throw error;
      return api('POST', '/work/entry/reports', body);
    });
    if (current.status === 'draft') current = await api('POST', `${path}/submit`, {}, { 'If-Match': current.version });
    if (current.status !== 'submitted') throw new Error(`Отчёт посева ${report.name} имеет статус ${current.status} вместо submitted.`);
    // Оба сотрудника без назначения на дату. Ночному отчёту добавляется вторая пометка серверной правкой.
    if (report.shift === 'night' && !current.marks.some(mark => mark.code === 'manager-edit')) {
      await api('PUT', path, { ...body, date: current.date, hours: 9, changeId: randomUUID() }, { 'If-Match': current.version });
    }
  }
  console.log('  + два поданных отчёта Work-21 с пометками');
}
