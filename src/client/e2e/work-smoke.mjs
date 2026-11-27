import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { BASE, callApi, callApiOk, clearSession, createChecks, launchBrowser, login, settled } from './harness.mjs';
import { WORK_PHOTO_NAME } from './seed-work.mjs';
import { WORK_HINT_MATERIALS } from './seed-work-materials.mjs';

function fixture(key) {
  const value = process.env[`SMOKE_WORK_${key}`];
  assert.ok(value, `Нужна SMOKE_WORK_${key} из e2e/seed.mjs --work.`);
  return value;
}

const [emailA, emailB, unlinked, manager, unassigned, executive, engineer] =
  ['A', 'B', 'UNLINKED', 'MANAGER', 'UNASSIGNED', 'EXECUTIVE', 'ENGINEER'].map(key => fixture(`${key}_EMAIL`));
const [site, reportA, reportB, day, photoB, zone] =
  ['SITE_ID', 'REPORT_A', 'REPORT_B', 'REPORT_B_DATE', 'PHOTO_B', 'TIME_ZONE'].map(fixture);
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
const { check, summarize } = createChecks();
const denied = [];
page.on('response', reply => {
  if (reply.status() === 403) denied.push(`${reply.request().method()} ${new URL(reply.url()).pathname} (${page.url()})`);
});

const heading = name => page.getByRole('heading', { name, exact: true });
const text = name => page.getByText(name, { exact: true });
const section = page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы', exact: true });
const own = { path: '/my-reports', name: 'Мои отчёты' };
const review = { path: '/work-review', name: 'Приёмка' };
const summary = { path: '/work-summary', name: 'Сводка' };
const queue = `${review.path}?constructionId=${site}`;
const summaryDay = `${summary.path}?${new URLSearchParams({ from: day, to: day })}`;
const laborMonth = fixture('LABOR_MONTH');
const ownerB = 'Монтажник Б (посев Work-25)';
const bRow = () => page.getByRole('row').filter({ hasText: ownerB });

function versioned(actor, method, path, version, body = null) {
  return actor.evaluate(async ({ method, path, version, body }) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const response = await fetch(`/api${path}`, { method, headers: {
      Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'If-Match': version,
    }, body: body === null ? undefined : JSON.stringify(body) });
    if (!response.ok) throw new Error(`${method} ${path} → ${response.status}: ${await response.text()}`);
  }, { method, path, version, body });
}

async function screens(expected) {
  await settled(page);
  if (expected.length) {
    await section.click();
    await heading(expected[0].name).waitFor();
  } else {
    assert.equal(await section.count(), 0, 'Инженеру ИД показан раздел «Работы».');
  }
  for (const screen of [own, review, summary]) {
    const link = page.getByRole('link', { name: screen.name, exact: true }).and(page.locator(`a[href="${screen.path}"]`));
    assert.equal(await link.count(), Number(expected.includes(screen)), `Видимость пункта «${screen.name}»`);
  }
}

async function ownList(expectedId, expectedStatus) {
  await page.goto(`${BASE}${own.path}`);
  const row = page.getByRole('row').filter({ hasText: 'Стройка проверки ролей Work-25 (посев)' });
  await row.getByText(expectedStatus, { exact: true }).waitFor();
  const feed = await callApiOk(page, 'GET', '/work/reports');
  assert.deepEqual(feed.items.map(item => item.id), [expectedId]);
}

async function photo(name = `Открыть фото: ${WORK_PHOTO_NAME}`) {
  const preview = page.getByRole('button', { name, exact: true }).locator('img');
  await preview.waitFor();
  await preview.evaluate(img => img.decode());
}

async function populatedSummary() {
  await page.goto(`${BASE}${summaryDay}`);
  await heading(summary.name).waitFor();
  await bRow().waitFor();
}

async function laborComparison() {
  const answer = await callApiOk(page, 'GET', `/constructions/${site}/progress?laborMonth=${laborMonth}`);
  const strip = answer.strips.find(item => item.code === 'work-labor');
  assert.equal(strip?.state, 'ok', strip?.text);
  assert.ok(strip.labor.estimatedHours > 0);
  assert.equal(strip.labor.actualHours, 8);
  await page.goto(`${BASE}/document-sets/${site}/progress`);
  const field = page.getByRole('group', { name: 'Месяц трудозатрат' });
  const month = field.getByRole('textbox', { name: /month|месяц/i });
  const yearField = field.getByRole('textbox', { name: /year|год/i });
  await month.waitFor();
  const [year, number] = laborMonth.split('-');
  if (await yearField.inputValue() !== year) await yearField.fill(year);
  if (await month.inputValue() !== number) await month.fill(number);
  const labor = page.getByRole('region', { name: 'Трудозатраты' });
  await labor.getByText('По смете', { exact: true }).waitFor();
  await labor.getByText('8 ч', { exact: true }).waitFor();
  await labor.getByText(/чел\.-ч$/).waitFor();
}

try {
  await check('work-installer-a-own-list-and-foreign-card', async () => {
    await login(page, emailA, password);
    await screens([own]);
    await ownList(reportA, 'Черновик');
    await page.goto(`${BASE}${own.path}/${reportB}`);
    await text('Отчёт не найден: его нет, или он вне ваших строек.').waitFor();
    assert.equal((await callApi(page, 'GET', `/work/photos/${photoB}/preview`)).status, 404);
  });

  await check('work-installer-gets-material-hint-tiers', async () => {
    const report = await callApiOk(page, 'GET', `/work/reports/${reportA}`);
    const [line] = report.lines;
    assert.ok(line, 'В черновике монтажника нет строки посева Work-26.');
    const query = new URLSearchParams({ constructionId: site, date: report.date, workTypeId: line.workTypeId,
      unitId: line.unitId, planItemId: line.workPlanItemId });
    const hint = await callApiOk(page, 'GET', `/work/materials/hint?${query}`);
    for (const [tier, name] of Object.entries(WORK_HINT_MATERIALS)) {
      assert.equal(hint[tier].state, 'ok', `Состояние яруса «${tier}».`);
      const item = hint[tier].items.find(item => item.displayName === name);
      assert.ok(item, `Ярус «${tier}» не содержит «${name}».`);
      assert.equal(Object.values(hint).flatMap(group => group.items).filter(other => other.id === item.id).length, 1);
      if (tier === 'estimate') assert.equal(item.notIssued, true);
      if (tier === 'issued') {
        assert.equal(item.unit, 'м');
        for (const key of ['quantity', 'first', 'last', 'waybillId']) assert.equal(Object.hasOwn(item, key), false);
      }
    }
    await page.goto(`${BASE}${own.path}/${reportA}`);
    const details = page.getByRole('button', { name: 'Подробности строки 1' });
    await details.waitFor();
    if (await details.getAttribute('aria-expanded') !== 'true') await details.click();
    await page.getByRole('button', { name: 'Добавить материал' }).click();
    const picker = page.getByRole('dialog', { name: 'Добавить материал' });
    await picker.getByRole('option').filter({ hasText: WORK_HINT_MATERIALS.issued }).click();
    await picker.getByRole('tab', { name: 'Смета' }).click();
    const resource = picker.getByRole('option').filter({ hasText: WORK_HINT_MATERIALS.estimate });
    await resource.getByText('Не отпущен').waitFor();
    await resource.click();
    await picker.getByRole('tab', { name: 'Справочник' }).click();
    await picker.getByRole('option').filter({ hasText: WORK_HINT_MATERIALS.catalog }).waitFor();
    await picker.getByRole('button', { name: 'Готово' }).click();
    await text(WORK_HINT_MATERIALS.issued).waitFor();
    await text(WORK_HINT_MATERIALS.estimate).waitFor();
    await page.getByRole('button', { name: 'Убрать материал 2' }).click();
    await page.getByRole('button', { name: 'Убрать материал 1' }).click();
  });

  await check('work-installer-edits-own-report-after-assignment-removal', async () => {
    const reportPath = `/work/reports/${reportA}`;
    const report = await callApiOk(page, 'GET', reportPath);
    const management = await browser.newPage();
    try {
      await login(management, manager, password);
      const { items } = await callApiOk(management, 'GET', `/work/assignments?constructionId=${site}`);
      const assigned = items.find(item => item.employeeId === report.employeeId && item.kind === 'installer');
      assert.ok(assigned, 'Не найдено назначение автора отчёта.');
      const { employeeId, constructionId, kind, dateFrom, dateTo } = assigned;
      await versioned(management, 'DELETE', `/work/assignments/${assigned.id}`, assigned.version);
      try {
        const access = await callApi(page, 'GET', `/work/materials/hint?constructionId=${site}&date=${report.date}`);
        assert.equal(access.status, 404);
        await page.goto(`${BASE}${own.path}/${reportA}`);
        const details = page.getByRole('button', { name: 'Подробности строки 1' });
        if (await details.getAttribute('aria-expanded') !== 'true') await details.click();
        await page.getByRole('button', { name: 'Добавить материал' }).click();
        const picker = page.getByRole('dialog', { name: 'Добавить материал' });
        await picker.getByRole('tab', { name: 'Справочник' }).click();
        await picker.getByRole('combobox', { name: 'Поиск материала' }).fill(WORK_HINT_MATERIALS.catalog);
        await picker.getByRole('option').filter({ hasText: WORK_HINT_MATERIALS.catalog }).click();
        await picker.getByRole('button', { name: 'Готово' }).click();
        await text(WORK_HINT_MATERIALS.catalog).waitFor();
        const [saved] = await Promise.all([
          page.waitForResponse(response => new URL(response.url()).pathname === `/api${reportPath}`
            && response.request().method() === 'PUT'),
          page.getByRole('button', { name: 'Сохранить', exact: true }).click(),
        ]);
        assert.equal(saved.status(), 200);
        const updated = await callApiOk(page, 'GET', reportPath);
        assert.equal(updated.lines[0].materials.length, report.lines[0].materials.length + 1);
        const { constructionId, date, shift, hours, note, lines } = report;
        await versioned(page, 'PUT', reportPath, updated.version, { constructionId, date, shift, hours, note, lines,
          changeId: randomUUID() });
      } finally {
        await callApiOk(management, 'POST', '/work/assignments', { employeeId, constructionId, kind, dateFrom, dateTo });
      }
    } finally { await management.close(); }
  });

  await check('work-installer-b-own-list-and-photo', async () => {
    await clearSession(page);
    await login(page, emailB, password);
    await screens([own]);
    await ownList(reportB, 'Подан');
    await page.goto(`${BASE}${own.path}/${reportB}`);
    await photo();
  });

  await check('work-site-today-in-its-own-time-zone', async () => {
    const access = await callApiOk(page, 'GET', '/account/access');
    assert.notEqual(zone, access.company.timeZone);
    const date = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' });
    const before = date.format(new Date());
    const sites = await callApiOk(page, 'GET', '/work/sites/mine');
    const assigned = sites.items.find(item => item.id === site);
    assert.ok(assigned, 'Монтажник Б не получил назначение своей стройки.');
    assert.equal(assigned.timeZoneId, zone);
    assert.ok([before, date.format(new Date())].includes(assigned.today), `Дата ${assigned.today} не соответствует ${zone}.`);
  });

  await check('work-unlinked-installer-sees-the-reason', async () => {
    await clearSession(page);
    await login(page, unlinked, password);
    await screens([own]);
    await text('Учётная запись не связана с сотрудником. Обратитесь к администратору.').waitFor();
    const access = await callApiOk(page, 'GET', '/account/access');
    assert.equal(access.employee, null);
    assert.equal(access.employeeReason, 'not-linked');
  });

  await check('work-assigned-manager-opens-queue-card-and-summary', async () => {
    await clearSession(page);
    await login(page, manager, password);
    await screens([review, summary]);
    await page.goto(`${BASE}${queue}`);
    await bRow().getByRole('link', { name: /Открыть отчёт/ }).click();
    await heading(ownerB).waitFor();
    await page.getByRole('button', { name: 'Принять', exact: true }).waitFor();
    await photo(/^Открыть фото 1:/);
    await populatedSummary();
    await laborComparison();
  });

  await check('work-unassigned-manager-sees-the-reason', async () => {
    await clearSession(page);
    await login(page, unassigned, password);
    await screens([review, summary]);
    await page.goto(`${BASE}${review.path}`);
    await text('Вы не назначены ни на одну стройку').waitFor();
    assert.equal(await bRow().count(), 0);
    await page.goto(`${BASE}${summaryDay}`);
    await text('Вы не назначены ни на одну стройку').waitFor();
    assert.equal(await bRow().count(), 0);
  });

  await check('work-executive-reads-without-actions', async () => {
    await clearSession(page);
    await login(page, executive, password);
    await screens([review, summary]);
    await page.goto(`${BASE}${queue}`);
    await text('все стройки').waitFor();
    await bRow().waitFor();
    await page.goto(`${BASE}${review.path}/${reportB}`);
    await heading(ownerB).waitFor();
    await photo(/^Открыть фото 1:/);
    for (const name of ['Принять', 'Вернуть', 'Изменить отчёт', 'Действия отчёта']) {
      assert.equal(await page.getByRole('button', { name, exact: true }).count(), 0, name);
    }
    await populatedSummary();
    await laborComparison();
  });

  await check('work-id-engineer-has-no-work-section', async () => {
    await clearSession(page);
    await login(page, engineer, password);
    await page.goto(`${BASE}/document-sets`);
    await page.getByRole('navigation', { name: 'Разделы' }).waitFor();
    await screens([]);
    const { permissions, modules } = await callApiOk(page, 'GET', '/account/access');
    assert.equal(modules.find(module => module.code === 'work')?.available, false);
    assert.deepEqual(permissions.filter(permission => permission.startsWith('work.') && permission !== 'work.facts.read'), []);
    // Контроль прямого отказа отдельным HTTP-клиентом, вне видимых действий страницы.
    const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
    const response = await page.request.get(`${BASE}/api/work/reports`, { headers: { Authorization: `Bearer ${token}` } });
    assert.equal(response.status(), 403);
  });

  await check('work-visible-actions-have-no-403', async () => {
    await settled(page);
    assert.deepEqual(denied, []);
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Роли Work');
