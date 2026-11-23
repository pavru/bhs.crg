import { createHash } from 'node:crypto';

export const SUMMARY_OTHER_SITE = 'Стройка для проверки сводки';
export const summaryEmployee = fixtureDay => `Елена Сводкина (посев ${fixtureDay})`;

export function summaryDays(today) {
  const start = new Date(`${today}T00:00:00Z`);
  return Array.from({ length: 4 }, (_, index) => {
    const date = new Date(start);
    date.setUTCDate(date.getUTCDate() - 3 + index);
    return date.toISOString().slice(0, 10);
  });
}

function reportId(fixtureDay, constructionId, date, shift) {
  const hash = createHash('sha256').update(`work-23:${fixtureDay}:${constructionId}:${date}:${shift}`).digest('hex');
  return `${hash.slice(0, 8)}-${hash.slice(8, 12)}-4${hash.slice(13, 16)}-8${hash.slice(17, 20)}-${hash.slice(20, 32)}`;
}

export async function seedSummary({ api, constructionId }) {
  const assignments = await api('GET', `/work/assignments?constructionId=${constructionId}`);
  const fixtureDay = assignments.construction.today;
  const [, from, past, today] = summaryDays(fixtureDay);
  // Один день посева — один сотрудник и свой набор отчётов. Соседние четырёхдневные периоды пересекаются.
  const displayName = summaryEmployee(fixtureDay);
  const employees = await api('GET', '/employees');
  const employee = employees.find(person => person.displayName === displayName)
    ?? await api('POST', '/employees', { displayName, data: JSON.stringify({ ТабельныйНомер: `E2E-24-${fixtureDay}` }) });
  if (!assignments.items.some(item => item.employeeId === employee.id && item.kind === 'installer'
    && item.dateFrom <= from && item.dateTo >= today)) {
    await api('POST', '/work/assignments', {
      constructionId, employeeId: employee.id, kind: 'installer', dateFrom: from, dateTo: today,
    });
  }
  const sites = await api('GET', '/constructions');
  const other = sites.find(site => site.name === SUMMARY_OTHER_SITE)
    ?? await api('POST', '/constructions', { name: SUMMARY_OTHER_SITE });

  for (const [site, date, shift, hours, submit] of [
    [constructionId, past, 'day', 8, true],
    [constructionId, past, 'night', 6, true],
    [constructionId, today, 'night', 5, false],
    [other.id, past, 'day', 0, true],
  ]) {
    const id = reportId(fixtureDay, site, date, shift);
    const path = `/work/entry/reports/${id}`;
    const report = await api('GET', path).catch(error => {
      if (!/ → 404 /.test(error.message)) throw error;
      return api('POST', '/work/entry/reports', { id, employeeId: employee.id, constructionId: site, date, shift, hours, lines: [] });
    });
    if (submit && report.status === 'draft') await api('POST', `${path}/submit`, {}, { 'If-Match': report.version });
  }
  console.log('  + сводка Work-23: две стройки, две смены, черновик и назначение без отчёта');
  return fixtureDay;
}
