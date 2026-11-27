import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { seedWorkMaterials } from './seed-work-materials.mjs';

const SITE = 'Стройка проверки ролей Work-25 (посев)';
const SEATS = {
  A: { email: 'work25-a@bhs.local', name: 'Монтажник А (посев Work-25)', role: 'Installer', number: 'E2E-W25-A' },
  B: { email: 'work25-b@bhs.local', name: 'Монтажник Б (посев Work-25)', role: 'Installer', number: 'E2E-W25-B' },
  LABOR: { email: 'work28-labor@bhs.local', name: 'Монтажник трудозатрат (посев Work-28)', role: 'Installer', number: 'E2E-W28-L' },
  UNLINKED: { email: 'work25-unlinked@bhs.local', name: 'Монтажник без связи (посев Work-25)', role: 'Installer' },
  MANAGER: { email: 'work25-manager@bhs.local', name: 'Менеджер стройки (посев Work-25)', role: 'ProjectManager', number: 'E2E-W25-M' },
  UNASSIGNED: { email: 'work25-unassigned@bhs.local', name: 'Менеджер без назначений (посев Work-25)', role: 'ProjectManager', number: 'E2E-W25-U' },
  EXECUTIVE: { email: 'work25-executive@bhs.local', name: 'Руководитель (посев Work-25)', role: 'Executive' },
};
const REPORT_A = '59a34fae-bd0a-4abf-a2fa-18cb4f234553';
const REPORT_B = '78a84820-a0bf-4a37-94cb-12d4273a2a96';
const LINE_A = 'b0771d96-7183-44ad-aa1a-a469db526195';
const LINE_B = 'adde5341-b9b5-4d2b-9183-bff6aa16a657';
const PHOTO_B = '2c12bb11-a268-4bf9-afa1-1346fd5c13df';
const LABOR_REPORT = 'f780c54b-77e7-4a65-a7e1-7232c13de3f3';
const LABOR_LINE = 'af229308-b168-4ffc-a2ec-2951bc3044c0';
export const WORK_PHOTO_NAME = 'Отчёт Б (посев Work-25).png';
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

async function ensureSite(api) {
  const { company } = await api('GET', '/account/access');
  const timeZoneId = company.timeZone === 'Asia/Vladivostok' ? 'Europe/Moscow' : 'Asia/Vladivostok';
  const all = await api('GET', '/constructions');
  const site = all.find(c => c.name === SITE) ?? await api('POST', '/constructions', { name: SITE });
  if (site.timeZoneId !== timeZoneId) await api('PUT', `/constructions/${site.id}/timezone`, { timeZoneId });
  return { id: site.id, timeZoneId };
}

async function ensureSeats(api, ensureUser, site) {
  for (const { email, name, role } of Object.values(SEATS)) await ensureUser(email, name, role);
  const users = await api('GET', '/users');
  const employees = await api('GET', '/employees');
  const { construction, items } = await api('GET', `/work/assignments?constructionId=${site.id}`);
  const dayAt = offset => new Date(Date.parse(construction.today) + offset * 86_400_000).toISOString().slice(0, 10);
  for (const [key, { name, email, number }] of Object.entries(SEATS)) {
    if (!number) continue;
    const employee = employees.find(e => e.displayName === name)
      ?? await api('POST', '/employees', { displayName: name, data: JSON.stringify({ ТабельныйНомер: number }) });
    const user = users.find(u => u.email.toLowerCase() === email);
    assert.ok(user, `Не найдена учётная запись ${email}`);
    if (employee.account?.state !== 'linked') {
      await api('PUT', `/employees/${employee.id}/account`, { userId: user.id }, { 'If-Match': employee.version });
    }
    if (key === 'UNASSIGNED') continue;
    const kind = key === 'MANAGER' ? 'manager' : 'installer';
    const assignment = items.find(a => a.employeeId === employee.id && a.kind === kind);
    if (!assignment) {
      await api('POST', '/work/assignments', { employeeId: employee.id, constructionId: site.id,
        kind, dateFrom: dayAt(-1), dateTo: kind === 'manager' ? null : dayAt(60) });
    } else if (assignment.dateTo && assignment.dateTo < dayAt(0)) {
      await api('PUT', `/work/assignments/${assignment.id}`, { dateFrom: assignment.dateFrom,
        dateTo: kind === 'manager' ? null : dayAt(60) }, { 'If-Match': assignment.version });
    }
  }
}

async function workLine(api) {
  const label = 'Работа проверки ролей Work-25 (посев)';
  const units = (await api('GET', '/document-types')).find(t => t.code === 'ЕдиницаИзмерения');
  assert.ok(units, 'Общий посев не завёл справочник единиц измерения.');
  const unit = (await api('GET', `/common-data?purpose=choice&typeId=${units.id}`)).find(e => e.displayName === 'м');
  assert.ok(unit, 'Общий посев не завёл единицу «м».');
  const types = await api('GET', `/work-types?purpose=choice&q=${encodeURIComponent(label)}`);
  const type = types.find(t => t.displayName === label) ?? await api('POST', '/work-types', {
    displayName: label,
    data: JSON.stringify({ Код: 'E2E-W25', Наименование: label,
      ЕдиницаИзмерения: { $ref: 'catalog', entryId: unit.id, displayName: unit.displayName } }),
  });
  return { id: LINE_B, workTypeId: type.id, unitId: unit.id, volume: 12, location: 'Участок Б (посев Work-25)',
    photos: [{ id: PHOTO_B, originalName: WORK_PHOTO_NAME }] };
}

async function ensureReport(api, site, id, lines) {
  const path = `/work/reports/${id}`;
  return api('GET', path).catch(async error => {
    if (!/ → 404 /.test(error.message)) throw error;
    const sites = await api('GET', '/work/sites/mine');
    const assigned = sites.items.find(item => item.id === site.id);
    assert.ok(assigned, `Нет назначения автора отчёта ${id}.`);
    assert.equal(assigned.timeZoneId, site.timeZoneId);
    const { date, shift } = assigned.suggested;
    return api('POST', '/work/reports', { id, constructionId: site.id, date, shift, hours: lines.length ? 8 : null, lines });
  });
}

export async function seedWork({ api, apiBase, token, findType, ensureUser, login, password, engineerEmail }) {
  const site = await ensureSite(api);
  await ensureSeats(api, ensureUser, site);
  const line = await workLine(api);
  await seedWorkMaterials({ api, apiBase, token, findType, site, line });
  const asInstaller = async seat => {
    const auth = { Authorization: `Bearer ${await login(seat.email, password)}` };
    return { auth, api: (method, path, body, headers) => api(method, path, body, { ...auth, ...headers }) };
  };
  const a = await asInstaller(SEATS.A);
  const draftLine = { ...line, id: LINE_A, location: 'Участок А (посев Work-26)', photos: [] };
  const reportA = await ensureReport(a.api, site, REPORT_A, [draftLine]);
  assert.equal(reportA.status, 'draft');
  if (!reportA.lines.length) {
    await a.api('PUT', `/work/reports/${REPORT_A}`, { changeId: randomUUID(), hours: 8, lines: [draftLine] },
      { 'If-Match': reportA.version });
  }
  const b = await asInstaller(SEATS.B);
  let reportB = await ensureReport(b.api, site, REPORT_B, [line]);
  const photo = reportB.lines.flatMap(row => row.photos).find(item => item.id === PHOTO_B);
  assert.ok(photo, 'В отчёте Б не объявлено фото посева.');
  if (photo.state !== 'stored') {
    const answer = await fetch(`${apiBase}/api/work/reports/${REPORT_B}/lines/${LINE_B}/photos/${PHOTO_B}`, {
      method: 'PUT', headers: { ...b.auth, 'Content-Type': 'image/png' }, body: PNG,
    });
    if (!answer.ok) throw new Error(`Загрузка фото посева → ${answer.status} ${await answer.text()}`);
  }
  if (reportB.status === 'draft') {
    reportB = await b.api('POST', `/work/reports/${REPORT_B}/submit`, { changeId: randomUUID() }, { 'If-Match': reportB.version });
  }
  assert.equal(reportB.status, 'submitted');
  assert.equal(reportB.photosPending, 0);
  const labor = await asInstaller(SEATS.LABOR);
  let laborReport = await ensureReport(labor.api, site, LABOR_REPORT, [{ ...line, id: LABOR_LINE, photos: [] }]);
  if (laborReport.status === 'draft') {
    laborReport = await labor.api('POST', `/work/reports/${LABOR_REPORT}/submit`, { changeId: randomUUID() },
      { 'If-Match': laborReport.version });
  }
  if (laborReport.status === 'submitted') {
    laborReport = await api('POST', `/work/reports/${LABOR_REPORT}/accept`, { changeId: randomUUID() },
      { 'If-Match': laborReport.version });
  }
  assert.equal(laborReport.status, 'accepted');
  for (const [key, { email }] of Object.entries(SEATS)) console.log(`SMOKE_WORK_${key}_EMAIL=${email}`);
  console.log([`SMOKE_WORK_ENGINEER_EMAIL=${engineerEmail}`, `SMOKE_WORK_SITE_ID=${site.id}`,
    `SMOKE_WORK_REPORT_A=${REPORT_A}`, `SMOKE_WORK_REPORT_B=${REPORT_B}`, `SMOKE_WORK_REPORT_B_DATE=${reportB.date}`,
    `SMOKE_WORK_PHOTO_B=${PHOTO_B}`, `SMOKE_WORK_TIME_ZONE=${site.timeZoneId}`,
    `SMOKE_WORK_LABOR_MONTH=${laborReport.date.slice(0, 7)}-01`].join('\n'));
}
