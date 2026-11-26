// Реальная сверка Work-27 под менеджером, читателем и инженером с правом контракта.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { BASE, callApi, callApiOk, createChecks, launchBrowser, login } from './harness.mjs';
import { ASSIGNMENT_PM } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL } from './seed-review.mjs';

const browser = await launchBrowser();
const { check, summarize } = createChecks();
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const suffix = randomUUID().slice(0, 8);
const works = 'Прокладка кабеля Work-27 ' + suffix;
const path = '/work/entry/reports';
const dateAt = (date, days) => new Date(Date.parse(date + 'T00:00:00Z') + days * 86_400_000).toISOString().slice(0, 10);

async function versioned(page, endpoint, version, body) {
  const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
  const response = await page.request.post(`${BASE}/api${endpoint}`, {
    headers: { Authorization: `Bearer ${token}`, 'If-Match': version },
    data: { changeId: randomUUID(), ...body },
  });
  assert.ok(response.ok(), `${endpoint} → ${response.status()} ${await response.text()}`);
  return response.json();
}

async function setup(admin) {
  const api = (method, endpoint, body) => callApiOk(admin, method, endpoint, body);
  const site = await api('POST', '/constructions', { name: 'Сверка Work-27 ' + suffix });
  const sections = [];
  for (const name of ['Корпус А', 'Корпус Б']) {
    sections.push(await api('POST', `/constructions/${site.id}/sections`, { name }));
  }
  const { construction } = await api('GET', `/work/assignments?constructionId=${site.id}`);
  const date = construction.today;
  const employees = await api('GET', '/employees');
  const manager = employees.find(e => e.account?.state === 'linked'
    && e.account.login?.toLowerCase() === ASSIGNMENT_PM.email.toLowerCase());
  assert.ok(manager, 'Менеджер посева не связан с сотрудником.');
  await api('POST', '/work/assignments', { constructionId: site.id, employeeId: manager.id,
    kind: 'manager', dateFrom: dateAt(date, -1), dateTo: null });
  const crew = [];
  for (const role of ['Бригадир', 'Участник', 'Поздняя подача']) {
    const name = `${role} Work-27 ${suffix}`;
    const employee = await api('POST', '/employees', { displayName: name, data: '{}' });
    await api('POST', '/work/assignments', { constructionId: site.id, employeeId: employee.id,
      kind: 'installer', dateFrom: dateAt(date, -1), dateTo: dateAt(date, 2) });
    crew.push(employee);
  }
  const types = await api('GET', '/document-types');
  const unitType = types.find(t => t.code === 'ЕдиницаИзмерения');
  assert.ok(unitType, 'Посев не завёл тип «ЕдиницаИзмерения».');
  const unit = (await api('GET', `/common-data?purpose=choice&typeId=${unitType.id}`)).find(item => item.displayName === 'м');
  assert.ok(unit, 'Посев не завёл единицу «м».');
  const type = await api('POST', '/work-types', {
    displayName: works,
    data: JSON.stringify({ Код: 'E2E-W27-' + suffix, Наименование: works,
      ЕдиницаИзмерения: { $ref: 'catalog', entryId: unit.id, displayName: unit.displayName } }),
  });
  const create = (employee, shift, sectionId, volume, participants = []) => api('POST', path, {
    id: randomUUID(), employeeId: employee.id, constructionId: site.id, date, shift, hours: 8,
    lines: [{ id: randomUUID(), workTypeId: type.id, unitId: unit.id, sectionId, volume, location: 'Оси 3–4',
      isCrew: true, crewRole: employee.id === crew[0].id ? 'lead' : 'member', participants }],
  });
  const reports = {
    lead: await create(crew[0], 'day', sections[0].id, 120, [crew[1].id, crew[2].id]),
    member: await create(crew[1], 'day', sections[0].id, 100),
    late: await create(crew[2], 'day', sections[0].id, 100),
    nightA: await create(crew[0], 'night', sections[0].id, 120),
    nightB: await create(crew[1], 'night', sections[1].id, 100),
  };
  const email = `work27-${suffix}@bhs.local`;
  await api('POST', '/users', { email, displayName: 'Инженер Work-27', password, roles: ['User'] });
  return { site, date, reports, email };
}

async function approve(page, group) {
  await group.getByRole('button', { name: 'Утвердить итог' }).click();
  const dialog = page.getByRole('dialog', { name: 'Утвердить итог бригады' });
  await dialog.getByRole('combobox', { name: 'Источник итогового объёма' }).click();
  await page.getByRole('option', { name: /120 м · Бригадир Work-27/ }).click();
  await dialog.getByRole('button', { name: 'Утвердить итог' }).click();
  await group.getByText('Утверждено', { exact: true }).waitFor();
}

try {
  const admin = await browser.newPage();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const reader = await browser.newPage({ viewport: { width: 390, height: 900 } });
  const facts = await browser.newPage();
  await login(admin);
  const { site, date, reports, email } = await setup(admin);
  const url = `${BASE}/work-review?tab=crew&crewConstructionId=${site.id}&crewFrom=${date}&crewTo=${date}`;
  const group = (tab, shift) => tab.getByRole('region', { name: works, exact: true }).filter({
    hasText: shift === 'night' ? '☾ ночь' : ' · день',
  });
  const submit = report => versioned(page, `${path}/${report.id}/submit`, report.version);
  const accept = report => versioned(page, `/work/reports/${report.id}/accept`, report.version);

  await login(page, ASSIGNMENT_PM.email, password);
  const lead = await submit(reports.lead);
  const member = await submit(reports.member);
  await accept(lead);
  const day = group(page, 'day');
  await check('crew-approved-waits-for-all-participating-reports', async () => {
    await page.goto(url);
    await approve(page, day);
    await day.getByText('Итог · 120 м', { exact: false }).waitFor();
    await day.getByText('Не включён в факт', { exact: true }).waitFor();
    await day.getByText('Принято отчётов · 1 из 2', { exact: true }).waitFor();
  });

  await check('crew-accepted-total-with-an-unsubmitted-person-is-visible-to-reader', async () => {
    await accept(member);
    await login(reader, REVIEW_READER_EMAIL, password);
    await reader.goto(url);
    const visible = group(reader, 'day');
    await visible.getByText('Включён в факт', { exact: true }).waitFor();
    await visible.getByText('Принято отчётов · 2 из 2', { exact: true }).waitFor();
    await visible.getByText('Не подали отчёт · 1', { exact: true }).waitFor();
    assert.equal(await visible.getByRole('button', { name: 'Утвердить итог' }).count(), 0);
    assert.equal(await visible.getByRole('link', { name: /Открыть отчёт/ }).count(), 2);
  });

  await check('crew-late-submission-invalidates-the-published-total', async () => {
    await submit(reports.late);
    await page.reload();
    await day.getByText('Требует решения', { exact: true }).waitFor();
    await day.getByText('Прежний итог · 120 м', { exact: false }).waitFor();
    await day.getByText('Не включён в факт', { exact: true }).waitFor();
    await day.getByText('Принято отчётов · 2 из 3', { exact: true }).waitFor();
  });

  await check('crew-two-sections-exclude-an-approved-total', async () => {
    await accept(await submit(reports.nightA));
    await accept(await submit(reports.nightB));
    await page.reload();
    const night = group(page, 'night');
    await approve(page, night);
    await night.getByText('Не включён в факт', { exact: true }).waitFor();
    await night.getByText('В строках группы разные позиции перечня.', { exact: true }).waitFor();
    const keys = night.getByRole('list', { name: 'Позиции перечня' });
    for (const section of ['Корпус А', 'Корпус Б']) {
      await keys.getByRole('listitem').filter({ hasText: section }).waitFor();
    }
    await reader.reload();
    await group(reader, 'night').getByText('Не включён в факт', { exact: true }).waitFor();
  });

  await check('crew-facts-only-cannot-open-reconciliation-or-report', async () => {
    await login(facts, email, password);
    const access = await callApiOk(facts, 'GET', '/account/access');
    assert.deepEqual(access.permissions.filter(permission => permission.startsWith('work.')), ['work.facts.read']);
    assert.equal(access.modules.find(module => module.code === 'work').available, false);
    assert.equal((await callApi(facts, 'GET', '/work/crew-groups?constructionId=' + site.id)).status, 403);
    assert.equal((await callApi(facts, 'GET', `/work/review/reports/${reports.lead.id}`)).status, 403);
    await facts.goto(url);
    await facts.getByText(/Раздел «Приёмка» недоступен|Раздел недоступен/).waitFor();
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Публикация бригадного факта');
