// Живой прогон очереди Work-21. Два отчёта и менеджера стройки заводит e2e/seed.mjs.
import assert from 'node:assert/strict';
import { BASE, clearSession, createChecks, launchBrowser, login } from './harness.mjs';
import { ASSIGNMENT_PM, FOREIGN_PM, INSTALLER } from './seed-assignments.mjs';
import { REVIEW_REPORTS } from './seed-review.mjs';

const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID;
if (!CONSTRUCTION) throw new Error('нет SMOKE_CONSTRUCTION_ID — запустите e2e/seed.mjs и передайте его переменные');

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
const { check, summarize } = createChecks();
const queue = `${BASE}/work-review?constructionId=${encodeURIComponent(CONSTRUCTION)}`;
const row = index => page.getByRole('row').filter({ hasText: REVIEW_REPORTS[index].name });

try {
  await login(page, ASSIGNMENT_PM.email, PASSWORD);
  await check('review-navigation-and-queue', async () => {
    await page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы' }).click();
    await page.getByRole('heading', { name: 'Приёмка', exact: true }).waitFor();
    await page.goto(queue);
    await page.getByText(/отчёты ваших строек \(1\)/).waitFor();
    await row(0).getByText('стройка не назначена на дату', { exact: true }).waitFor();
    await row(1).getByText('правка менеджером', { exact: true }).waitFor();
    await row(1).getByText('☾ ночь', { exact: true }).waitFor();
  });

  await check('review-mark-filter', async () => {
    const request = page.waitForRequest(req => {
      const url = new URL(req.url());
      return url.pathname === '/api/work/review/queue' && url.searchParams.get('mark') === 'manager-edit';
    });
    await page.getByRole('button', { name: /правка менеджером.*1/ }).click();
    assert.equal(new URL((await request).url()).searchParams.get('constructionId'), CONSTRUCTION);
    await row(1).waitFor();
    await row(0).waitFor({ state: 'detached' });
    await page.getByRole('button', { name: 'Сбросить отбор', exact: true }).click();
    await row(0).waitFor();
    await row(1).waitFor();
  });

  await clearSession(page);
  await login(page, FOREIGN_PM.email, PASSWORD);
  await check('review-queue-empty-for-foreign-manager', async () => {
    await page.goto(queue);
    await page.getByText('Учётная запись не связана с сотрудником', { exact: true }).waitFor();
    assert.equal(await row(0).count(), 0);
    assert.equal(await row(1).count(), 0);
  });

  await clearSession(page);
  await login(page, INSTALLER.email, PASSWORD);
  await check('installer-has-no-review-navigation', async () => {
    assert.equal(await page.getByRole('link', { name: 'Работы', exact: true }).count(), 0);
    await page.goto(queue);
    await page.getByText(/Раздел «Приёмка» недоступен|Раздел недоступен/).waitFor();
    assert.equal(await row(0).count(), 0);
    assert.equal(await row(1).count(), 0);
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Приёмка отчётов');
