import assert from 'node:assert/strict';
import { BASE, callApiOk, createChecks, launchBrowser, login } from './harness.mjs';
import { ASSIGNMENT_PM, ENTRY_EMPLOYEE, INSTALLER } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL } from './seed-review.mjs';

const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const browser = await launchBrowser();
const { check, summarize } = createChecks();
const heading = (page, name) => page.getByRole('heading', { name, exact: typeof name === 'string' });
const button = (page, name) => page.getByRole('button', { name, exact: true });

async function createOrOpen(page, path) {
  const response = page.waitForResponse(reply => new URL(reply.url()).pathname === `/api${path}`
    && reply.request().method() === 'POST');
  await button(page, 'Создать черновик').click();
  const status = (await response).status();
  if (status === 409) {
    await page.getByText('На эту смену отчёт уже есть.').waitFor();
    await button(page, 'Открыть').click();
  } else {
    assert.equal(status, 201);
  }
}

try {
  await check('installer-creates-and-opens-existing-draft', async () => {
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    try {
      await login(page, INSTALLER.email, password);
      await heading(page, 'Мои отчёты').waitFor();
      const sites = await callApiOk(page, 'GET', '/work/sites/mine');
      const site = sites.items[0];
      assert.ok(site?.suggested.label);
      await button(page, site.suggested.label).click();
      await heading(page, 'Новый отчёт').waitFor();
      await page.getByText(sites.boundary.text, { exact: true }).waitFor();
      assert.equal(await page.getByRole('tab', { name: site.suggested.shift === 'night' ? 'Ночь' : 'День' })
        .getAttribute('aria-selected'), 'true');
      await createOrOpen(page, '/work/reports');
      await page.waitForURL(/\/my-reports\/[0-9a-f-]{36}$/);
      const opened = page.url();
      const saved = await callApiOk(page, 'GET', `/work/reports/${opened.split('/').at(-1)}`);
      assert.equal(saved.date, site.suggested.date);
      assert.equal(saved.shift, site.suggested.shift);
      await button(page, 'К отчётам').click();
      await heading(page, 'Мои отчёты').waitFor();
      await page.getByRole('row').filter({ hasText: site.name }).getByText(/Подастся .* по времени стройки/).first().waitFor();
      await button(page, site.suggested.label).click();
      await button(page, 'Создать черновик').click();
      await page.getByText('На эту смену отчёт уже есть.').waitFor();
      await button(page, 'Открыть').click();
      await page.waitForURL(opened);
    } finally { await page.close(); }
  });

  await check('manager-creates-for-assigned-employee', async () => {
    const page = await browser.newPage();
    try {
      await login(page, ASSIGNMENT_PM.email, password);
      await page.goto(`${BASE}/work-review`);
      await button(page, 'Ввести за сотрудника').click();
      await page.getByRole('checkbox', { name: `Отметить: ${ENTRY_EMPLOYEE.name}` }).check();
      await button(page, 'Продолжить').click();
      await page.getByText(`Вводит ${ASSIGNMENT_PM.name} за сотрудника: ${ENTRY_EMPLOYEE.name}`).waitFor();
      await page.getByText('стройки сотрудника в вашем охвате (1)').waitFor();
      await createOrOpen(page, '/work/entry/reports');
      await page.waitForURL(/\/work-review\/entry\/[0-9a-f-]{36}$/);
      const id = page.url().split('/').at(-1);
      const report = await callApiOk(page, 'GET', `/work/entry/reports/${id}`);
      assert.equal(report.employeeName, ENTRY_EMPLOYEE.name);
      assert.equal(report.enteredBy.name, ASSIGNMENT_PM.name);
    } finally { await page.close(); }
  });

  await check('executive-has-review-without-report-entry', async () => {
    const page = await browser.newPage();
    try {
      await login(page, REVIEW_READER_EMAIL, password);
      await page.goto(`${BASE}/work-review`);
      await heading(page, 'Приёмка').waitFor();
      assert.equal(await page.getByRole('link', { name: 'Мои отчёты', exact: true }).count(), 0);
      assert.equal(await button(page, 'Ввести за сотрудника').count(), 0);
    } finally { await page.close(); }
  });
} finally {
  await browser.close();
}
process.exitCode = summarize('Веб-ввод отчётов');
