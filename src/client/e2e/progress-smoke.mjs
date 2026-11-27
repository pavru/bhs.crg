// Plan-25 на настоящем API: тот же набор запускается с Work и без него.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { BASE, callApi, callApiOk, createChecks, launchBrowser, login } from './harness.mjs';

const suffix = randomUUID().slice(0, 8);
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const { check, summarize } = createChecks();
const browser = await launchBrowser();
const entry = '/work/entry/reports';
const desktop = { width: 1280, height: 900 };
const smallVolume = 0.0001;
const smallText = /^0[,.]0001\sм$/;
const largeVolume = 12_345_678.125;
const largeText = /^12[\s,]345[\s,]678[,.]125\sм$/;

async function createUser(admin, label, role) {
  const email = `p25-${label}-${suffix}@bhs.local`;
  await callApiOk(admin, 'POST', '/users', { email, displayName: `Plan-25 ${label}`, password, roles: [role] });
  return email;
}

async function contractRole(admin, title, permissions) {
  const role = await callApiOk(admin, 'POST', '/roles', {
    title: `Plan-25 ${title} ${suffix}`, permissions: ['core.constructions.read', ...permissions],
  });
  return createUser(admin, title, role.name);
}

async function versioned(page, endpoint, version) {
  const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
  const response = await page.request.post(`${BASE}/api${endpoint}`, {
    headers: { Authorization: `Bearer ${token}`, 'If-Match': version }, data: { changeId: randomUUID() },
  });
  assert.ok(response.ok(), `${endpoint} → ${response.status()} ${await response.text()}`);
  return response.json();
}

async function prepare(admin, workEnabled) {
  const api = (method, endpoint, body) => callApiOk(admin, method, endpoint, body);
  const site = await api('POST', '/constructions', { name: `Ход работ Plan-25 ${suffix}` });
  const first = await api('POST', `/constructions/${site.id}/sections`, { name: 'Первый корпус' });
  const second = await api('POST', `/constructions/${site.id}/sections`, { name: 'Второй корпус' });
  const unitType = (await api('GET', '/document-types')).find(item => item.code === 'ЕдиницаИзмерения');
  assert.ok(unitType, 'Запустите посев и перезапустите API, чтобы появился классификатор.');
  const unit = (await api('GET', `/common-data?purpose=choice&typeId=${unitType.id}`)).find(item => item.displayName === 'м');
  assert.ok(unit, 'Нужна единица «м» из посева.');
  const makeType = (label, key) => api('POST', '/work-types', {
    displayName: label,
    data: JSON.stringify({ Код: `P25-${key}-${suffix}`, Наименование: label,
      ЕдиницаИзмерения: { $ref: 'catalog', entryId: unit.id, displayName: unit.displayName } }),
  });
  const measured = await makeType(`Прокладка кабеля Plan-25 ${suffix}`, 'actual');
  const extra = await makeType(`Несметная маркировка Plan-25 ${suffix}`, 'extra');
  const pending = await makeType(`Бригадная прокладка Plan-25 ${suffix}`, 'crew');
  const graph = await api('POST', '/plan/schedules', {
    constructionId: site.id, creationKey: randomUUID(), name: `График Plan-25 ${suffix}`,
  });
  const date = '2026-12-01';
  for (const [type, section] of [[measured, first], [pending, second]]) {
    await api('POST', `/plan/schedules/${graph.id}/works`, {
      workTypeId: type.id, sectionId: section.id, work: { volume: 75, start: date, end: date, employeeIds: [] },
    });
  }
  if (!workEnabled) return { site, first, second, graph };

  const { construction } = await api('GET', `/work/assignments?constructionId=${site.id}`);
  const employee = await api('POST', '/employees', { displayName: `Монтажник Plan-25 ${suffix}`, data: '{}' });
  await api('POST', '/work/assignments', {
    constructionId: site.id, employeeId: employee.id, kind: 'installer',
    dateFrom: construction.today, dateTo: construction.today,
  });
  const line = (type, section, volume, crew = false) => ({
    id: randomUUID(), workTypeId: type.id, unitId: unit.id, sectionId: section.id, volume, location: 'Оси 3–4',
    ...(crew ? { isCrew: true, crewRole: 'lead', participants: [] } : {}),
  });
  const draft = await api('POST', entry, {
    id: randomUUID(), employeeId: employee.id, constructionId: site.id, date: construction.today, shift: 'day', hours: 8,
    lines: [line(measured, first, smallVolume), line(extra, second, largeVolume), line(pending, second, 937.75, true)],
  });
  const submitted = await versioned(admin, `${entry}/${draft.id}/submit`, draft.version);
  await versioned(admin, `/work/reports/${draft.id}/accept`, submitted.version);
  return { site, first, second, graph, employee, measured, extra, pending, reportId: draft.id };
}

try {
  const admin = await browser.newPage();
  await login(admin);
  const access = await callApiOk(admin, 'GET', '/account/access');
  assert(access.modules.some(module => module.code === 'plan'), 'Для сценария нужен План.');
  const workEnabled = access.modules.some(module => module.code === 'work');
  if (process.env.SMOKE_PROGRESS_WORK) {
    assert.equal(workEnabled, process.env.SMOKE_PROGRESS_WORK === 'on', 'Выбран не тот состав модулей.');
  }
  const fixture = await prepare(admin, workEnabled);
  const { site, first, second, graph } = fixture;
  const parent = `/document-sets/${site.id}`;
  const path = `${parent}/progress`;
  const apiPath = `/constructions/${site.id}/progress`;
  const title = page => page.getByRole('heading', { name: 'Ход работ', exact: true });
  const warning = page => page.getByText('Диагностика факта по всей стройке за весь период', { exact: true });
  const work = (page, name) => page.getByRole('row').filter({ has: page.getByRole('rowheader', { name: new RegExp(name) }) });
  const estimate = await createUser(admin, 'estimator', 'Estimator');
  const manager = await createUser(admin, 'manager', 'ProjectManager');
  const denied = await contractRole(admin, 'denied', []);
  const estimatorPage = await browser.newPage();
  const managerPage = await browser.newPage({ viewport: desktop });
  const deniedPage = await browser.newPage();
  await login(estimatorPage, estimate, password);
  await login(managerPage, manager, password);
  await login(deniedPage, denied, password);

  await check('progress-roles-and-module-state', async () => {
    for (const page of [estimatorPage, managerPage]) {
      await page.goto(BASE + parent);
      await page.waitForURL(url => url.pathname === path);
      await title(page).waitFor();
      if (workEnabled) {
        await page.getByRole('columnheader', { name: /Факт по учёту/ }).waitFor();
      } else {
        await page.getByText('Учёт работ не подключён', { exact: true }).waitFor();
        assert.equal(await page.getByRole('columnheader', { name: /Факт/ }).count(), 0);
      }
    }
    await estimatorPage.getByText('costs.totals.read', { exact: true }).waitFor();
    if (workEnabled) await estimatorPage.getByRole('status').getByText('work.facts.read', { exact: true }).waitFor();
    assert.equal(await estimatorPage.getByText(/Разнесено.*оплачено/).count(), 0);
    await managerPage.getByText(/Разнесено.*оплачено/).waitFor();
    await managerPage.getByText('Привязано к работам · не ведётся', { exact: true }).waitFor();
    const result = await callApi(deniedPage, 'GET', apiPath);
    assert.equal(result.status, 403);
    await deniedPage.goto(BASE + path);
    await deniedPage.getByRole('heading', { name: 'Раздел «Ход работ» недоступен' }).waitFor();
    assert.equal(await deniedPage.getByRole('table').count(), 0);
  });

  if (workEnabled) {
    const { employee, measured, extra, pending, reportId } = fixture;
    const factsEmail = await contractRole(admin, 'facts', ['work.facts.read']);
    const facts = await browser.newPage({ viewport: { width: 390, height: 844 } });
    await login(facts, factsEmail, password);
    await check('current-fact-section-and-desktop-inspector', async () => {
      await managerPage.goto(BASE + path);
      const table = managerPage.getByRole('table');
      const inspector = managerPage.getByRole('complementary', { name: 'Подробности работы' });
      await work(managerPage, extra.displayName).click();
      await inspector.getByRole('heading', { name: extra.displayName }).waitFor();
      await inspector.getByText(largeText).waitFor();
      assert.equal(await table.getByText(/937[,.]75/).count(), 0);
      const selection = managerPage.getByRole('combobox', { name: 'Раздел стройки' });
      await selection.click();
      await managerPage.getByRole('option', { name: 'Первый корпус' }).click();
      await work(managerPage, measured.displayName).getByText(smallText).waitFor();
      await inspector.getByRole('heading', { name: measured.displayName }).waitFor();
      await warning(managerPage).waitFor();
      await managerPage.getByText(/Бригадных групп.*итог не утверждён: 1/).waitFor();
      assert.equal(await work(managerPage, extra.displayName).count(), 0);
      await managerPage.goto(`${BASE}${parent}/sections/${second.id}/progress`);
      await work(managerPage, extra.displayName).getByText(largeText).waitFor();
      assert.equal(await managerPage.getByRole('combobox', { name: 'Раздел стройки' }).count(), 0);
    });
    await check('mobile-keeps-plan-and-fact-in-the-same-viewport', async () => {
      await managerPage.setViewportSize({ width: 390, height: 844 });
      try {
        await managerPage.goto(BASE + path);
        const row = work(managerPage, measured.displayName);
        await row.getByText('Срок по графику', { exact: false }).waitFor();
        await row.getByText(smallText).waitFor();
        const table = managerPage.getByRole('table');
        await table.scrollIntoViewIfNeeded();
        for (const name of [/План по смете/, /Факт по учёту/]) {
          const cell = await table.getByRole('columnheader', { name }).boundingBox();
          assert(cell && cell.x >= 0 && cell.x + cell.width <= 390, `Колонка ${name} вышла за экран`);
        }
        const longAmount = work(managerPage, extra.displayName).getByText(largeText);
        assert(await longAmount.evaluate(element => {
          const range = document.createRange();
          range.selectNodeContents(element);
          const cell = element.closest('td').getBoundingClientRect();
          return Array.from(range.getClientRects()).every(rect => rect.left >= cell.left && rect.right <= cell.right);
        }), 'Длинное значение вышло за пределы ячейки');
        assert.equal(await managerPage.getByRole('complementary', { name: 'Подробности работы' }).count(), 0);
      } finally {
        await managerPage.setViewportSize(desktop);
      }
    });
    await check('contract-reader-mobile-has-only-fact', async () => {
      const grants = await callApiOk(facts, 'GET', '/account/access');
      assert.deepEqual(grants.permissions.filter(p => p.startsWith('work.')), ['work.facts.read']);
      assert.equal(grants.modules.find(module => module.code === 'work').available, false);
      assert.equal((await callApi(facts, 'GET', `/work/review/reports/${reportId}`)).status, 403);
      const data = await callApiOk(facts, 'GET', `${apiPath}?section=${first.id}`);
      assert.equal(data.costs.state, 'hidden');
      assert.equal('totals' in data.costs, false);
      assert.equal(data.rows.find(row => row.workTypeId === measured.id).cells.fact.volume, smallVolume);
      assert.equal(data.factDiagnostics.excludedCrewGroups, 1);
      const labor = data.strips.find(strip => strip.code === 'work-labor');
      assert.equal(labor.state, 'hidden');
      assert.equal(labor.permission, 'work.report.read');
      assert.equal('labor' in labor, false);
      await facts.goto(BASE + path);
      await work(facts, measured.displayName).getByText(smallText).waitFor();
      await work(facts, extra.displayName).getByText(largeText).waitFor();
      await warning(facts).waitFor();
      await facts.getByRole('region', { name: 'Трудозатраты' }).getByText(/work\.report\.read/).waitFor();
      assert.equal(await facts.getByRole('complementary', { name: 'Подробности работы' }).count(), 0);
      assert.equal(await facts.getByText(/Разнесено.*оплачено|937[,.]75/).count(), 0);
      assert.equal(await facts.getByText(employee.displayName, { exact: true }).count(), 0);
      assert.equal(await facts.locator(`a[href*="${reportId}"]`).count(), 0);
      assert.equal(await work(facts, pending.displayName).count(), 0);
    });
  }

  await check('schedule-current-fact-or-disconnected', async () => {
    await managerPage.goto(`${BASE}${parent}/schedule/${graph.id}`);
    await managerPage.getByRole('heading', { name: graph.name }).waitFor();
    const cards = managerPage.getByRole('list', { name: 'Работы графика' });
    if (workEnabled) {
      const measured = cards.getByRole('listitem').filter({ hasText: fixture.measured.displayName });
      const pending = cards.getByRole('listitem').filter({ hasText: fixture.pending.displayName });
      await measured.getByText('Факт · включённый объём', { exact: true }).waitFor();
      await measured.getByText(smallText).waitFor();
      await measured.getByText('Данных мало', { exact: true }).waitFor();
      await pending.getByLabel('Нет включённого объёма').waitFor();
      await pending.getByText('0 отчётов · 0 строек', { exact: true }).waitFor();
      await warning(managerPage).waitFor();
    } else {
      await cards.getByText('Учёт работ не подключён', { exact: true }).first().waitFor();
      assert.equal(await cards.getByText(/^Факт/).count(), 0);
    }
  });
} finally {
  await browser.close();
}
process.exitCode = summarize('Plan-25 · Ход работ');
