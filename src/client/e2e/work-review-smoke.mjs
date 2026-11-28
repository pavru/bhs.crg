// Живой прогон очереди и карточки Work-21. Два отчёта и менеджера стройки заводит e2e/seed.mjs.
import assert from 'node:assert/strict';
import { BASE, callApi, clearSession, createChecks, launchBrowser, login } from './harness.mjs';
import { ASSIGNMENT_PM, FOREIGN_PM, INSTALLER } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL, REVIEW_REPORTS, REVIEW_RETURN_RECIPIENT_EMAIL } from './seed-review.mjs';

const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID;
if (!CONSTRUCTION) throw new Error('нет SMOKE_CONSTRUCTION_ID — запустите e2e/seed.mjs и передайте его переменные');

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
const { check, summarize } = createChecks();
const queue = `${BASE}/work-review?constructionId=${encodeURIComponent(CONSTRUCTION)}`;
const row = index => page.getByRole('row').filter({ hasText: REVIEW_REPORTS[index].name });
const direct = index => `${BASE}/work-review/${REVIEW_REPORTS[index].id}`;
const dialog = () => page.getByRole('dialog');
const returnedReport = `${BASE}/my-reports/${REVIEW_REPORTS[1].id}`;
const returnComment = 'Уточните место работ.';

async function managerChecks() {
  await login(page, ASSIGNMENT_PM.email, PASSWORD);
  await check('review-navigation-and-queue', async () => {
    await page.getByRole('navigation', { name: 'Разделы' }).getByRole('link', { name: 'Работы' }).click();
    await page.getByRole('heading', { name: 'Приёмка', exact: true }).waitFor();
    await page.goto(queue);
    await page.getByText(/отчёты ваших строек \(1\)/).waitFor();
    await row(0).getByText('стройка не назначена на дату', { exact: true }).waitFor();
    await row(0).getByText(/поздняя доставка .*действие .*UTC.*приём .*UTC/).waitFor();
    await page.getByRole('button', { name: /поздняя доставка.*1/ }).waitFor();
    await row(1).getByText('правка менеджером', { exact: true }).waitFor();
    await row(1).getByText('☾ ночь', { exact: true }).waitFor();
  });

  await check('review-mark-filter', async () => {
    const request = page.waitForRequest(req => {
      const url = new URL(req.url());
      return url.pathname === '/api/work/review/queue' && url.searchParams.get('mark') === 'manager-edit';
    });
    const chip = page.getByRole('button', { name: /правка менеджером.*1/ });
    await chip.focus();
    await chip.press('Enter');
    assert.equal(new URL((await request).url()).searchParams.get('constructionId'), CONSTRUCTION);
    await row(1).waitFor();
    await row(0).waitFor({ state: 'detached' });
    assert.equal(await chip.evaluate(button => button === document.activeElement), true);
    await page.getByRole('button', { name: 'Сбросить отбор', exact: true }).click();
    await row(0).waitFor();
    await row(1).waitFor();
  });

  await check('review-manager-opens-card-and-accepts', async () => {
    await page.goto(queue);
    await row(0).getByRole('link', { name: /Открыть отчёт/ }).click();
    await page.getByRole('heading', { name: REVIEW_REPORTS[0].name }).waitFor();
    await page.getByText(/поздняя доставка .*действие .*UTC.*приём .*UTC/).waitFor();
    await page.getByRole('button', { name: 'Принять', exact: true }).click();
    await dialog().getByRole('button', { name: 'Принять', exact: true }).click();
    await page.getByText('Принят', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'К очереди приёмки' }).click();
    assert.equal(new URL(page.url()).searchParams.get('constructionId'), CONSTRUCTION);
    await row(1).waitFor();
    await row(0).waitFor({ state: 'detached' });
  });

  await check('review-manager-returns-with-comment', async () => {
    await row(1).getByRole('link', { name: /Открыть отчёт/ }).click();
    await page.getByRole('button', { name: 'Вернуть', exact: true }).click();
    const submit = dialog().getByRole('button', { name: 'Вернуть', exact: true });
    assert.equal(await submit.isDisabled(), true);
    await dialog().getByRole('textbox', { name: 'Что исправить' }).fill(returnComment);
    await submit.click();
    await dialog().waitFor({ state: 'hidden' });
    await page.getByRole('region', { name: 'Журнал отчёта' }).getByText(returnComment).waitFor();
    await page.getByRole('button', { name: 'Принять', exact: true, includeHidden: true }).waitFor({ state: 'detached' });
  });
}

try {
  await login(page, FOREIGN_PM.email, PASSWORD);
  await check('review-queue-empty-for-foreign-manager', async () => {
    await page.goto(queue);
    await page.getByText('Учётная запись не связана с сотрудником', { exact: true }).waitFor();
    assert.equal(await row(0).count(), 0);
    assert.equal(await row(1).count(), 0);
  });
  await check('review-foreign-card-has-same-404', async () => {
    await page.goto(direct(0));
    await page.getByText('Отчёт не найден: его нет, или он вне ваших строек', { exact: true }).waitFor();
  });

  await clearSession(page);
  await login(page, REVIEW_READER_EMAIL, PASSWORD);
  await check('review-executive-can-filter-named-employees', async () => {
    await page.goto(queue);
    await page.getByText('все стройки', { exact: true }).waitFor();
    await row(0).waitFor();
    await row(1).waitFor();
    assert.equal((await callApi(page, 'GET', '/employees')).status, 403);
    await page.getByRole('combobox', { name: 'Сотрудник', exact: true }).click();
    await page.getByRole('option', { name: REVIEW_REPORTS[1].name, exact: true }).click();
    await row(0).waitFor({ state: 'detached' });
    await row(1).waitFor();
  });
  await check('review-executive-card-is-read-only', async () => {
    await page.goto(direct(1));
    await page.getByRole('heading', { name: REVIEW_REPORTS[1].name }).waitFor();
    await page.getByRole('heading', { name: 'Журнал' }).waitFor();
    for (const name of ['Принять', 'Вернуть', 'Действия отчёта'])
      assert.equal(await page.getByRole('button', { name, exact: true }).count(), 0);
  });

  await clearSession(page);
  await login(page, INSTALLER.email, PASSWORD);
  await check('installer-has-no-review-navigation', async () => {
    await page.getByRole('heading', { name: 'Мои отчёты', exact: true }).waitFor();
    assert.equal(await page.getByRole('link', { name: 'Приёмка', exact: true }).count(), 0);
    await page.goto(queue);
    await page.getByText(/Раздел «Приёмка» недоступен|Раздел недоступен/).waitFor();
    assert.equal(await row(0).count(), 0);
    assert.equal(await row(1).count(), 0);
  });
  await clearSession(page);
  await managerChecks();

  await clearSession(page);
  await login(page, REVIEW_RETURN_RECIPIENT_EMAIL, PASSWORD);
  await check('review-return-notification-opens-own-report', async () => {
    await page.getByRole('button', { name: /Уведомления.*новых уведомлений: [1-9]/ }).click();
    const bell = page.getByRole('region', { name: 'Уведомления', exact: true });
    await bell.getByText(/Отчёт за .* возвращён/).waitFor();
    assert.equal(await bell.getByText(returnComment, { exact: true }).count(), 0);
    await bell.getByRole('button', { name: 'Открыть отчёт', exact: true }).click();
    await page.waitForURL(returnedReport);
    await page.getByText(returnComment, { exact: true }).waitFor();
  });

  await clearSession(page);
  await login(page, INSTALLER.email, PASSWORD);
  await check('review-return-notification-and-report-are-private', async () => {
    const notifications = await callApi(page, 'GET', '/notifications');
    assert.equal(notifications.status, 200);
    assert.equal(notifications.body.items.some(item => item.linkUrl === new URL(returnedReport).pathname), false);
    await page.goto(returnedReport);
    await page.getByText(/Отчёт не найден/).waitFor();
    assert.equal(await page.getByText(returnComment, { exact: true }).count(), 0);
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Приёмка отчётов');
