// Живая сводка Work-23: посев создаёт смены и назначения, стенд отдаёт настоящий API.
import assert from 'node:assert/strict';
import { BASE, callApi, callApiOk, clearSession, createChecks, launchBrowser, login, pickOption } from './harness.mjs';
import { ASSIGNMENT_PM, FOREIGN_PM, INSTALLER } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL } from './seed-review.mjs';
import { SUMMARY_OTHER_SITE, summaryDays, summaryEmployee } from './seed-summary.mjs';

const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const construction = process.env.SMOKE_CONSTRUCTION_ID;
const fixtureDay = process.env.SMOKE_SUMMARY_DAY;
if (!construction || !fixtureDay) throw new Error('Нужны SMOKE_CONSTRUCTION_ID и SMOKE_SUMMARY_DAY из e2e/seed.mjs.');
const employee = summaryEmployee(fixtureDay);
const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1280, height: 960 } });
const { check, summarize } = createChecks();
const grid = () => page.getByRole('table');
const row = () => grid().getByRole('row').filter({ has: page.getByRole('rowheader', { name: employee, exact: true }) });
const nav = () => page.getByRole('link', { name: 'Сводка', exact: true });

try {
  await login(page, ASSIGNMENT_PM.email, password);
  const [first, , , last] = summaryDays(fixtureDay);
  const search = new URLSearchParams({ from: first, to: last });
  const address = `${BASE}/work-summary?${search}`;

  await check('summary-manager-navigation-and-states', async () => {
    await page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы' }).click();
    await nav().waitFor();
    await page.goto(address);
    await page.getByText('отчёты ваших строек (1)', { exact: true }).waitFor();
    const visible = await callApiOk(page, 'GET', `/work/summary?${search}`);
    assert.deepEqual(visible.constructions.map(item => item.id), [construction]);
    assert.ok(visible.rows.every(item => item.constructionId === construction));
    const cells = row().getByRole('cell');
    await cells.nth(1).getByText('Не назначен', { exact: true }).waitFor();
    await cells.nth(2).getByText('Не поступал', { exact: true }).waitFor();
    await cells.nth(1).getByRole('group').press('ArrowRight');
    assert.equal(await cells.nth(2).getByRole('group').evaluate(cell => cell === document.activeElement), true);
    await page.getByRole('region', { name: 'Подробности клетки' }).getByText(/Не поступал/).waitFor();
    assert.equal(await cells.nth(3).getByRole('link').count(), 2);
    assert.equal(await cells.nth(3).getByText('☾', { exact: true }).count(), 1);
    assert.equal(await cells.nth(4).getByRole('link').count(), 0);
    await cells.nth(4).getByRole('button', { name: /Ночная смена.*Черновик/ }).click();
    assert.equal(new URL(page.url()).pathname, '/work-summary');
    await page.getByRole('region', { name: 'Подробности клетки' }).getByText(/Автоподача.*по времени стройки/).waitFor();
    await cells.nth(3).getByRole('link', { name: /Дневная смена/ }).click();
    await page.getByRole('heading', { name: employee, exact: true }).waitFor();
  });

  await check('summary-focus-visible-after-horizontal-scroll', async () => {
    const start = new Date(`${last}T00:00:00Z`);
    start.setUTCDate(start.getUTCDate() - 30);
    const month = new URLSearchParams({ from: start.toISOString().slice(0, 10), to: last });
    await page.goto(`${BASE}/work-summary?${month}`);
    const right = row().getByRole('group').nth(19);
    await right.evaluate(cell => {
      const table = cell.closest('table');
      const scroller = table.parentElement;
      const sticky = table.querySelector('thead th[data-pin="1"]');
      cell.focus({ preventScroll: true });
      scroller.scrollLeft += cell.getBoundingClientRect().left - sticky.getBoundingClientRect().right - 2;
    });
    await page.keyboard.press('ArrowLeft');
    const left = row().getByRole('group').nth(18);
    assert.ok(await left.evaluate(cell => {
      const sticky = cell.closest('table').querySelector('thead th[data-pin="1"]');
      return cell === document.activeElement && cell.getBoundingClientRect().left >= sticky.getBoundingClientRect().right - 1;
    }), 'сфокусированная клетка должна быть видна справа от закреплённых колонок');
  });

  await check('summary-mobile-one-day-without-horizontal-scroll', async () => {
    await page.setViewportSize({ width: 390, height: 880 });
    await page.goto(address);
    await grid().getByRole('columnheader', { name: 'Стройка' }).waitFor();
    assert.equal(await grid().getByRole('columnheader').count(), 3);
    assert.equal(await grid().evaluate(table => table.scrollWidth <= table.parentElement.clientWidth + 1), true);
    await pickOption(page.getByRole('combobox', { name: 'День', exact: true }), last.split('-').reverse().join('.'));
    const secondRow = grid().getByRole('row').nth(2);
    const secondName = await secondRow.getByRole('rowheader').innerText();
    await secondRow.getByRole('group').focus();
    await page.getByRole('button', { name: 'Предыдущий день' }).click();
    assert.equal(await grid().getByRole('columnheader').count(), 3);
    assert.ok((await page.getByRole('region', { name: 'Подробности клетки' }).getByRole('heading').innerText()).startsWith(`${secondName} ·`));
    await row().getByRole('link', { name: /Ночная смена/ }).waitFor();
    await page.setViewportSize({ width: 1280, height: 960 });
  });

  await clearSession(page);
  await login(page, FOREIGN_PM.email, password);
  await check('summary-unassigned-project-manager', async () => {
    await page.goto(address);
    await page.getByText('Вы не назначены ни на одну стройку').waitFor();
    assert.equal(await grid().count(), 0);
    assert.equal((await callApi(page, 'GET', `/work/summary?${search}&construction=${construction}`)).status, 404);
  });

  await clearSession(page);
  await login(page, REVIEW_READER_EMAIL, password);
  await check('summary-executive-sees-both-sites-and-can-filter', async () => {
    await page.goto(address);
    await page.getByText('все стройки', { exact: true }).waitFor();
    assert.equal(await row().count(), 2);
    const filtered = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/work/summary' && !!url.searchParams.get('construction');
    });
    await pickOption(page.getByRole('combobox', { name: 'Стройка' }), SUMMARY_OTHER_SITE);
    assert.equal((await filtered).status(), 200);
    await row().getByText(SUMMARY_OTHER_SITE, { exact: true }).waitFor();
    assert.equal(await row().count(), 1);
    assert.ok(new URL(page.url()).searchParams.get('construction'));
  });

  await clearSession(page);
  await login(page, INSTALLER.email, password);
  await check('summary-installer-is-denied', async () => {
    await page.getByRole('heading', { name: 'Мои отчёты', exact: true }).waitFor();
    await page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы', exact: true }).waitFor();
    assert.equal(await nav().count(), 0);
    assert.equal((await callApi(page, 'GET', `/work/summary?${search}`)).status, 403);
    await page.goto(address);
    await page.getByRole('heading', { name: 'Раздел «Сводка» недоступен', exact: true }).waitFor();
    assert.equal(await grid().count(), 0);
  });
} finally {
  await browser.close();
}
process.exitCode = summarize('Сводка');
