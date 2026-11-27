// Исправление принятого отчёта браузером. Посев и вторая запись при конфликте идут через настоящий API.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { BASE, callApiOk, createChecks, launchBrowser, login, pickOption } from './harness.mjs';
import { FOREIGN_PM } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL } from './seed-review.mjs';

const browser = await launchBrowser();
const { check, summarize } = createChecks();
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const suffix = randomUUID().slice(0, 8);
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const fieldKey = 'work32Act';
const fieldTitle = 'Номер акта Work-32';
const pathOf = id => `/work/entry/reports/${id}`;
const cardOf = id => `/work/review/reports/${id}`;
const dayAt = (today, days) => new Date(Date.parse(`${today}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10);

async function raw(page, method, path, data, headers = {}) {
  const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
  return page.request.fetch(`${BASE}/api${path}`, { method, data, headers: { Authorization: `Bearer ${token}`, ...headers } });
}

async function json(page, method, path, data, headers) {
  const response = await raw(page, method, path, data, headers);
  assert.ok(response.ok(), `${method} ${path} → ${response.status()} ${await response.text()}`);
  return response.json();
}

async function configureDetails(admin) {
  const all = await callApiOk(admin, 'GET', '/document-types');
  const details = all.find(type => type.code === 'СтрокаРаботыСведения');
  assert.ok(details, 'Тип сведений строки должен быть спроецирован после посева и перезапуска API.');
  const path = `/document-types/${details.id}`;
  const { schema } = await callApiOk(admin, 'GET', path);
  const saved = typeof schema === 'string' ? schema : JSON.stringify(schema);
  const parsed = JSON.parse(saved);
  const fields = parsed.fields.filter(field => field.key !== fieldKey);
  fields.push({ key: fieldKey, title: fieldTitle, type: 'string', forAcceptance: true });
  await callApiOk(admin, 'PUT', `${path}/schema`, { schema: JSON.stringify({ ...parsed, fields }) });
  return () => callApiOk(admin, 'PUT', `${path}/schema`, { schema: saved });
}

async function setup(admin) {
  const api = (method, path, body) => callApiOk(admin, method, path, body);
  const site = await api('POST', '/constructions', { name: `Исправление Work-32 ${suffix}` });
  const { construction } = await api('GET', `/work/assignments?constructionId=${site.id}`);
  const date = dayAt(`${construction.today.slice(0, 7)}-01`, -1);
  const from = `${date.slice(0, 7)}-01`;
  const email = `manager-work32-${suffix}@bhs.local`;
  const displayName = `Менеджер Work-32 ${suffix}`;
  const account = await api('POST', '/users', { email, displayName, password, roles: ['ProjectManager'] });
  const manager = await api('POST', '/employees', { displayName, data: '{}' });
  const linked = await raw(admin, 'PUT', `/employees/${manager.id}/account`, { userId: account.id }, { 'If-Match': manager.version });
  assert.ok(linked.ok(), `Связь менеджера → ${linked.status()} ${await linked.text()}`);
  await api('POST', '/work/assignments', { constructionId: site.id, employeeId: manager.id, kind: 'manager', dateFrom: date });
  const types = await api('GET', '/document-types');
  const unitType = types.find(type => type.code === 'ЕдиницаИзмерения');
  const unit = (await api('GET', `/common-data?purpose=choice&typeId=${unitType.id}`)).find(item => item.displayName === 'м');
  const workType = async key => {
    const label = `Работа ${key} Work-32 ${suffix}`;
    return api('POST', '/work-types', { displayName: label, data: JSON.stringify({ Код: `E2E-W32-${key}-${suffix}`,
      Наименование: label, ЕдиницаИзмерения: { $ref: 'catalog', entryId: unit.id, displayName: unit.displayName } }) });
  };
  const soloType = await workType('отчёта');
  const crewType = await workType('бригады');
  const line = (type, volume, extra = {}) => ({ id: randomUUID(), workTypeId: type.id, unitId: unit.id, volume, ...extra });
  const photo = { id: randomUUID(), originalName: 'До закрытия.png' };
  const late = { id: randomUUID(), originalName: 'После закрытия.png' };
  async function report(name, shift, lines) {
    const employee = await api('POST', '/employees', { displayName: `${name} Work-32 ${suffix}`, data: '{}' });
    await api('POST', '/work/assignments', { constructionId: site.id, employeeId: employee.id, kind: 'installer', dateFrom: date, dateTo: date });
    const id = randomUUID();
    const body = { id, employeeId: employee.id, constructionId: site.id, date, shift, hours: 8, lines };
    const draft = await api('POST', '/work/entry/reports', body);
    for (const row of lines) if (row.photos?.some(item => item.id === photo.id)) {
      await upload(admin, id, row.id, photo.id);
    }
    const submitted = await json(admin, 'POST', `${pathOf(id)}/submit`, {}, { 'If-Match': draft.version });
    await json(admin, 'POST', `/work/reports/${id}/accept`, { changeId: randomUUID() }, { 'If-Match': submitted.version });
    return { body, employee };
  }
  const solo = await report('Монтажник', 'night', [line(soloType, 120, { photos: [late] }), line(soloType, 20, { photos: [photo] })]);
  const crew = await report('Бригадир', 'day', [line(crewType, 120, { isCrew: true, crewRole: 'lead' })]);
  const groupsPath = `/work/crew-groups?constructionId=${site.id}&from=${date}&to=${date}`;
  const group = (await api('GET', groupsPath)).items[0];
  const approved = await json(admin, 'POST', `/work/crew-groups/${group.id}/approve`, { total: 120 }, { 'If-Match': group.version });
  assert.equal(approved.publication.published, true);
  const contour = { contour: 'construction', constructionId: site.id, from, through: date };
  const preview = await api('POST', '/periods/close/preview', contour);
  assert.equal(preview.canClose, true);
  await api('POST', '/periods/close', { ...contour, ifMatch: 'none', report: preview.stamp });
  await upload(admin, solo.body.id, solo.body.lines[0].id, late.id);
  return { site, date, from, email, solo, crew, groupsPath, crewType, photo, late };
}

async function upload(page, report, line, id) {
  const response = await raw(page, 'PUT', `/work/reports/${report}/lines/${line}/photos/${id}`, png, { 'Content-Type': 'image/png' });
  assert.ok(response.ok(), `Фото → ${response.status()} ${await response.text()}`);
}

const button = (page, name) => page.getByRole('button', { name, exact: true });
const field = (page, name) => page.getByRole('textbox', { name, exact: true });
const reasonField = page => page.getByRole('textbox', { name: /^Причина исправления/ });
async function save(page, reason) {
  await button(page, 'Проверить изменения').click();
  await reasonField(page).fill(reason);
  await button(page, 'Сохранить новой версией').click();
}

let restoreDetails;
try {
  const admin = await browser.newPage();
  const page = await browser.newPage({ viewport: { width: 1280, height: 960 } });
  const reader = await browser.newPage();
  const foreign = await browser.newPage();
  await login(admin);
  restoreDetails = await configureDetails(admin);
  const { site, date, from, email, solo, crew, groupsPath, crewType, photo, late } = await setup(admin);
  await login(page, email, password);
  const access = await callApiOk(page, 'GET', '/account/access');
  assert.ok(access.permissions.includes('work.report.review') && !access.permissions.includes('work.sites.all'));
  const soloUrl = `${BASE}/work-review/${solo.body.id}`;
  await check('closed-report-corrects-from-summary-and-preserves-snapshot', async () => {
    await page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы' }).click();
    await page.getByRole('link', { name: 'Сводка', exact: true }).click();
    await page.goto(`${BASE}/work-summary?construction=${site.id}&from=${from}&to=${date}`);
    await page.getByRole('row').filter({ hasText: solo.employee.displayName }).getByRole('link', { name: /Ночная смена/ }).click();
    await page.waitForURL(soloUrl);
    await page.getByText(/не заполнены поля/).first().waitFor();
    await button(page, 'Исправить отчёт').click();
    await field(page, fieldTitle).first().fill('А-17');
    await field(page, 'Часы за смену').fill('6');
    await button(page, 'Убрать строку 2').click();
    const reason = 'Дописан акт, исправлен табель';
    await save(page, reason);
    await page.getByText('Версия 2 сохранена.').waitFor();
    await page.getByText('А-17', { exact: true }).waitFor();
    assert.equal(await page.getByText(/не заполнены поля/).count(), 0);
    await page.getByText('Период закрыт', { exact: true }).waitFor();
    await page.getByRole('region', { name: 'Журнал отчёта' }).getByText(reason).waitFor();
    const current = (await callApiOk(page, 'GET', cardOf(solo.body.id))).report;
    assert.equal(current.status, 'accepted');
    assert.equal(current.hours, 6);
    assert.deepEqual(current.lines.map(line => line.id), [solo.body.lines[0].id]);
    assert.equal(current.lines[0].photos.find(item => item.id === late.id).state, 'stored');
    assert.equal((await raw(page, 'PUT', `/work/reports/${solo.body.id}/lines/${current.lines[0].id}/photos/${randomUUID()}`,
      png, { 'Content-Type': 'image/png' })).status(), 409);
    assert.equal((await raw(page, 'PUT', pathOf(solo.body.id), solo.body, { 'If-Match': current.version })).status(), 409);
    await pickOption(page.getByRole('combobox', { name: 'Версия отчёта' }), 'Версия 1 · на момент закрытия');
    assert.equal(new URL(page.url()).searchParams.get('number'), '1');
    await page.getByText('Нужно к приёмке').first().waitFor();
    assert.equal(await button(page, 'Исправить отчёт').count(), 0);
    const older = (await callApiOk(page, 'GET', `${cardOf(solo.body.id)}?number=1`)).report;
    assert.equal(older.hours, 8);
    assert.equal(older.lines[0].photos[0].state, 'pending');
    const historicalPhoto = older.lines[1].photos.find(item => item.id === photo.id);
    assert.equal((await raw(page, 'GET', historicalPhoto.versionUrl.replace('/api', ''))).status(), 200);
    await page.reload();
    await page.getByText('Нужно к приёмке').first().waitFor();
    await pickOption(page.getByRole('combobox', { name: 'Версия отчёта' }), 'Версия 2 · действующая');
    await page.getByText('А-17', { exact: true }).waitFor();
  });

  await check('closed-report-conflict-keeps-draft-and-uses-existing-table-link', async () => {
    await page.goto(`${BASE}/tables/work.reports`);
    await page.getByRole('row').filter({ hasText: solo.employee.displayName })
      .locator(`a[href="/work-review/${solo.body.id}"]`).click();
    await page.waitForURL(soloUrl);
    await button(page, 'Исправить отчёт').click();
    await field(page, fieldTitle).fill('А-18');
    const latest = (await callApiOk(admin, 'GET', cardOf(solo.body.id))).report;
    const { constructionId, date, shift, hours, lines } = latest;
    await json(admin, 'POST', `/work/reports/${solo.body.id}/correction`, {
      constructionId, date, shift, hours, lines: lines.map(({ photos: _photos, ...line }) => line),
      note: 'Уточнено вторым менеджером', reason: 'Добавлено примечание',
    }, { 'If-Match': latest.version });
    await save(page, 'Получен акт А-18');
    await button(page, 'Перечитать и сверить').click();
    await button(page, 'Подтвердить новую основу').click();
    assert.equal(await field(page, fieldTitle).inputValue(), 'А-18');
    assert.equal(await field(page, 'Примечание к отчёту').inputValue(), 'Уточнено вторым менеджером');
    await button(page, 'Проверить изменения').click();
    assert.equal(await reasonField(page).inputValue(), 'Получен акт А-18');
    await button(page, 'Сохранить новой версией').click();
    await page.getByText('Версия 4 сохранена.').waitFor();
  });

  await check('closed-crew-correction-excludes-stale-fact', async () => {
    const progress = () => callApiOk(admin, 'GET', `/constructions/${site.id}/progress`);
    const factOf = report => report.rows.find(row => row.workTypeId === crewType.id)?.cells.fact?.volume;
    assert.equal(factOf(await progress()), 120);
    await page.goto(`${BASE}/work-review/${crew.body.id}`);
    await button(page, 'Исправить отчёт').click();
    await field(page, 'Объём, строка 1').fill('90');
    await save(page, 'Объём по акту');
    await page.getByText('Версия 2 сохранена.').waitFor();
    const { items: [group] } = await callApiOk(page, 'GET', groupsPath);
    assert.equal(group.state, 'stale');
    assert.equal(group.publication.published, false);
    assert.equal(factOf(await progress()) ?? null, null);
    const old = (await callApiOk(page, 'GET', `${cardOf(crew.body.id)}?number=1`)).report;
    assert.equal(old.lines[0].volume, 120);
  });

  await check('closed-report-respects-reader-and-manager-reach', async () => {
    await login(reader, REVIEW_READER_EMAIL, password);
    await reader.goto(soloUrl);
    await reader.getByRole('heading', { name: solo.employee.displayName }).waitFor();
    assert.equal(await button(reader, 'Исправить отчёт').count(), 0);
    assert.equal((await raw(reader, 'POST', `/work/reports/${solo.body.id}/correction`, {})).status(), 403);
    await login(foreign, FOREIGN_PM.email, password);
    await foreign.goto(soloUrl);
    await foreign.getByText('Отчёт не найден: его нет, или он вне ваших строек').waitFor();
    assert.equal((await raw(foreign, 'POST', `/work/reports/${solo.body.id}/correction`, {})).status(), 404);
  });
} finally {
  try { await restoreDetails?.(); } finally { await browser.close(); }
}

process.exitCode = summarize('Веб-исправление закрытого отчёта');
