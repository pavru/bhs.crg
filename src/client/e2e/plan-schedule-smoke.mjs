// Plan-20. График из синтетической сметы, сохранение работ, связь с соседним графиком и реальный PDF.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { BASE, EMAIL, PASSWORD, callApi, callApiOk, createChecks, launchBrowser, login } from './harness.mjs';
import { EMPLOYEES } from './seed-employees.mjs';
import { NEIGHBOR_SCHEDULE, SCHEDULE_ESTIMATE } from './seed-schedules.mjs';

const constructionId = process.env.SMOKE_CONSTRUCTION_ID;
const estimateId = process.env.SMOKE_SCHEDULE_ESTIMATE_ID;
const neighborId = process.env.SMOKE_NEIGHBOR_SCHEDULE_ID;
const managerEmail = process.env.SMOKE_PM_EMAIL || 'pm@bhs.local';
const estimatorEmail = process.env.SMOKE_ESTIMATOR_EMAIL || 'smeta@bhs.local';
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const viewport = { width: 1500, height: 1000 };
const sectionPath = `/document-sets/${constructionId}`;
const schedulesPath = '/plan/schedules';
const phaseName = 'Подготовка для графика (посев Plan-20)';
const employee = EMPLOYEES[0].name;
if (!constructionId || !estimateId || !neighborId) throw new Error(
  'Нужен посев Plan-20. Передайте SMOKE_CONSTRUCTION_ID, SMOKE_SCHEDULE_ESTIMATE_ID и SMOKE_NEIGHBOR_SCHEDULE_ID из seed.mjs.');

const button = (scope, name) => scope.getByRole('button', { name, exact: true });
const tab = (scope, name) => scope.getByRole('tab', { name, exact: true });
const { check, summarize } = createChecks();
const browser = await launchBrowser();
const errors = [];

async function select(page, scope, field, option) {
  await scope.getByRole('combobox', { name: field, exact: true }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}

async function requestDuring(page, method, path, action, status = 200) {
  const [response] = await Promise.all([
    page.waitForResponse(response => response.request().method() === method && new URL(response.url()).pathname === `/api${path}`),
    action(),
  ]);
  if (response.status() !== status) throw new Error(`${method} ${path} → ${response.status()}: ${(await response.text()).slice(0, 350)}`);
  return response;
}

async function putMatch(page, path, version, workTypeId) {
  return page.evaluate(async ({ path, version, workTypeId }) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const response = await fetch(`/api${path}`, { method: 'PUT', headers: {
      Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'If-Match': version,
    }, body: JSON.stringify({ workTypeId, factor: 100, remember: false }) });
    if (!response.ok) throw new Error(`Сопоставление посева → ${response.status}: ${await response.text()}`);
    return (await response.json()).version;
  }, { path, version, workTypeId });
}

async function prepare(admin) {
  const api = (method, path, body) => callApiOk(admin, method, path, body);
  const estimate = await api('GET', `/plan/estimates/${estimateId}`);
  const neighbor = await api('GET', `${schedulesPath}/${neighborId}`);
  assert.equal(estimate.name, SCHEDULE_ESTIMATE);
  assert.equal(neighbor.name, NEIGHBOR_SCHEDULE);
  assert.equal(estimate.constructionId, constructionId);
  assert.equal(neighbor.constructionId, constructionId);

  const unitType = (await api('GET', '/document-types')).find(type => type.code === 'ЕдиницаИзмерения');
  const piece = unitType && (await api('GET', `/common-data?purpose=choice&typeId=${unitType.id}`)).find(item => item.displayName === 'шт');
  assert(piece, 'Нужна единица «шт» из посева.');
  const types = await api('GET', `/work-types?purpose=choice&q=${encodeURIComponent(phaseName)}`);
  const type = types.find(item => item.displayName === phaseName) ?? await api('POST', '/work-types', {
    displayName: phaseName, data: JSON.stringify({ Код: 'E2E-P20', Наименование: phaseName,
      ЕдиницаИзмерения: { $ref: 'catalog', entryId: piece.id, displayName: piece.displayName } }),
  });
  const revision = estimate.revisions.find(item => item.id === estimate.currentRevisionId);
  const rows = (await api('GET', `/plan/revisions/${revision.id}/rows`)).items.filter(row => row.kind === 'work').slice(0, 2);
  assert.equal(rows.length, 2);
  let version = revision.version;
  for (const row of rows) {
    if (row.workTypeId) assert.equal(row.workTypeId, type.id);
    else version = await putMatch(admin, `/plan/revisions/${revision.id}/lines/${row.id}/match`, version, type.id);
  }
  const predecessor = neighbor.works.find(row => row.workTypeId === type.id) ?? await api('POST',
    `${schedulesPath}/${neighbor.id}/works`, {
      workTypeId: type.id, work: { volume: 3, start: '2026-12-01', end: '2026-12-07' },
    });
  return { revision, predecessor };
}

async function fillDate(page, label, digits) {
  const group = page.getByRole('group', { name: label, exact: true });
  await group.getByRole('textbox', { name: 'день', exact: true }).click();
  await page.keyboard.type(digits);
}

try {
  const admin = await browser.newPage({ viewport });
  await login(admin, EMAIL, PASSWORD);
  const fixture = await prepare(admin);
  await admin.close();
  const manager = await browser.newPage({ viewport });
  manager.on('pageerror', error => errors.push(error.message));
  await login(manager, managerEmail, password);
  const graphName = `График Plan-20 ${Date.now()}`;
  let schedule;
  let selected;

  await check('manager-builds-schedule-from-estimate', async () => {
    await manager.goto(`${BASE}${sectionPath}/estimates`);
    await button(manager, 'График').click();
    await button(manager, 'Построить из сметы').click();
    const form = manager.getByRole('dialog', { name: 'Построить из сметы' });
    await form.getByRole('textbox', { name: 'Название графика' }).fill(graphName);
    await select(manager, form, 'Смета', SCHEDULE_ESTIMATE);
    schedule = await (await requestDuring(manager, 'POST', schedulesPath, () => button(form, 'Построить').click(), 201)).json();
    assert.equal(schedule.revisionId, fixture.revision.id);
    assert.equal(schedule.works.length, 2);
    assert(schedule.skipped.unmapped > 0, 'В посеве должны быть несопоставленные строки.');
    await manager.getByRole('heading', { name: graphName, exact: true }).waitFor();
    await manager.getByText(/Не вошли? \d+ несопоставленн/).waitFor();
    selected = schedule.works[0];
  });

  if (selected) {
    const graphPath = `${schedulesPath}/${schedule.id}`;
    await check('manager-saves-dates-volume-and-assignee', async () => {
      await button(manager, `Действия работы «${selected.name}»`).press('Enter');
      await manager.getByRole('menuitem', { name: 'Изменить', exact: true }).press('Enter');
      const volume = manager.getByRole('textbox', { name: 'Объём', exact: true });
      await volume.waitFor();
      await manager.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Объём');
      await volume.fill('125');
      await fillDate(manager, 'Начало', '01122026');
      await fillDate(manager, 'Окончание', '02122026');
      await button(manager, 'Выбрать исполнителей').click();
      const picker = manager.getByRole('dialog', { name: 'Исполнители' });
      await picker.getByRole('checkbox', { name: `Отметить: ${employee}` }).check();
      await button(picker, 'Выбрать').click();
      assert(await button(manager, 'Печать списка…').isDisabled());
      const saved = await requestDuring(manager, 'PUT', `${graphPath}/works/${selected.id}`,
        () => button(manager, 'Сохранить работу').click());
      assert.equal(await saved.request().headerValue('if-match'), selected.version);
      const work = await saved.json();
      assert.equal(work.start, '2026-12-01');
      assert.equal(work.end, '2026-12-02');
      assert.equal(work.volume, 125);
      assert.equal(work.assignees[0].name, employee);
      assert.equal(work.durationWorkingDays, 2);
      await manager.getByText(employee, { exact: true }).waitFor();
    });

    await check('manager-links-neighbor-and-finds-warning-in-both-views', async () => {
      await button(manager, `Предшественники: ${selected.name}`).click();
      const form = manager.getByRole('dialog', { name: `Предшественники · ${selected.name}` });
      await select(manager, form, 'График предшественника', NEIGHBOR_SCHEDULE);
      await select(manager, form, 'Предшественник', fixture.predecessor.name);
      await form.getByRole('textbox', { name: 'Задержка' }).fill('1');
      const link = await (await requestDuring(manager, 'POST', `${graphPath}/links`,
        () => button(form, 'Сохранить связь').click(), 201)).json();
      assert.deepEqual([link.predecessorId, link.successorId, link.lag], [fixture.predecessor.id, selected.id, 1]);
      await form.getByText(`${fixture.predecessor.name} · По стройке в целом`, { exact: true }).waitFor();
      await button(form, 'Закрыть').click();
      const filter = manager.getByRole('button', { name: /^Нарушения зависимостей · 1$/ });
      await filter.click();
      await manager.getByRole('list', { name: 'Работы графика' }).getByRole('listitem')
        .filter({ hasText: selected.name }).getByRole('img').waitFor();
      const datesPath = `/plan/calendars/constructions/${constructionId}/non-working-days`;
      const range = await (await requestDuring(manager, 'GET', datesPath, () => tab(manager, 'Диаграмма').click())).json();
      assert(range.nonWorkingDays.length > 0);
      await manager.locator(`[data-non-working-date="${range.nonWorkingDays[0]}"]`).waitFor();
      await manager.getByRole('list', { name: 'Связи за пределами диаграммы' }).getByText(/связь между графиками/).waitFor();
      await button(manager, `Открыть в списке: ${selected.name}`).click();
      assert.equal(await tab(manager, 'Список').getAttribute('aria-selected'), 'true');
    });

    await check('manager-previews-and-downloads-real-pdf', async () => {
      const preview = await requestDuring(manager, 'POST',
        `/print/${encodeURIComponent('ГрафикСтройки')}/${schedule.id}/preview`, () => button(manager, 'Печать списка…').click());
      assert.match(preview.headers()['content-type'], /^application\/pdf\b/);
      assert.equal((await preview.body()).subarray(0, 4).toString(), '%PDF');
      const [download] = await Promise.all([manager.waitForEvent('download'), button(manager, 'Скачать PDF').click()]);
      assert.equal(download.suggestedFilename(), `${graphName}.pdf`);
      assert.equal((await readFile(await download.path())).subarray(0, 4).toString(), '%PDF');
      await button(manager.getByRole('dialog'), 'Закрыть').click();
      await manager.setViewportSize({ width: 390, height: 900 });
      await tab(manager, 'Диаграмма').waitFor({ state: 'hidden' });
      await manager.getByRole('list', { name: 'Работы графика' }).waitFor();
    });
  }

  const estimator = await browser.newPage({ viewport });
  estimator.on('pageerror', error => errors.push(error.message));
  await login(estimator, estimatorEmail, password);
  await check('estimator-cannot-open-or-print-schedule', async () => {
    await estimator.goto(`${BASE}${sectionPath}/estimates`);
    await estimator.getByRole('link', { name: `Открыть смету «${SCHEDULE_ESTIMATE}»`, exact: true }).waitFor();
    assert.equal(await button(estimator, 'График').count(), 0);
    assert.equal((await callApi(estimator, 'GET', `${schedulesPath}/${neighborId}`)).status, 403);
    assert.equal((await callApi(estimator, 'POST', `/print/${encodeURIComponent('ГрафикСтройки')}/${neighborId}/preview`)).status, 403);
  });
  await check('no-browser-errors', () => assert.deepEqual(errors, []));
} finally {
  await browser.close();
}
process.exitCode = summarize('График стройки и печать списка');
