// Индикатор состояния счёта (costs-invoice-screen-11, ТЗ COST-6.4) глазами живого прогона: фраза в сводке,
// пункты перечня «Состояние счёта», действие пункта. Сообщения о счёте больше не стоят плашками в форме —
// прогоны, которые их проверяли, читают их отсюда.
import { until } from './harness.mjs';

/** Кнопка индикатора в сводке открытого счёта. */
export const indicator = page => page.getByTestId('invoice-header').getByTestId('invoice-status');

/** Фраза индикатора — как она видна в сводке. */
export const statusPhrase = async page => (await indicator(page).innerText()).replace(/\s+/g, ' ').trim();

const sheetOf = page => page.getByRole('dialog', { name: 'Состояние счёта' });

/** Открыть перечень; `run` — что сделать в нём; лист закрывается, если `run` не закрыл его сам. */
export async function inStatusSheet(page, run) {
  await indicator(page).click();
  const sheet = sheetOf(page);
  await sheet.waitFor({ timeout: 10_000 });
  try { return await run(sheet); } finally {
    // Действие пункта закрывает лист само; Escape — только если он остался: иначе он закрыл бы то, что
    // действие открыло (вопрос о несохранённом).
    await sheet.waitFor({ state: 'detached', timeout: 1_000 }).catch(() => {});
    if (await sheet.count()) { await page.keyboard.press('Escape'); await sheet.waitFor({ state: 'detached', timeout: 5_000 }); }
  }
}

/** Пункты перечня: `{ key, text }` в порядке листа. */
export const statusItems = page => inStatusSheet(page, sheet => sheet.locator('[data-testid^="status-item-"]').evaluateAll(
  els => els.map(e => ({ key: e.dataset.testid.slice('status-item-'.length), text: e.textContent.replace(/\s+/g, ' ').trim() }))));

/** Ждать, пока перечень не станет таким, как ждали (`test(items)`), — с описанием увиденного в отказе. */
export async function untilStatus(page, test, what, timeout = 10_000) {
  let seen = [];
  await until(async () => {
    seen = await statusItems(page);
    if (!test(seen)) throw new Error('ещё нет');
  }, { timeout, every: 400 }).catch(() => {
    throw new Error(`${what}; в перечне: ${JSON.stringify(seen.map(i => `${i.key}: ${i.text}`))}`);
  });
  return seen;
}

/** Нажать действие пункта `key` в перечне (лист закрывается действием). */
export const statusAction = (page, key, name) => inStatusSheet(page, sheet =>
  sheet.getByTestId(`status-item-${key}`).locator('xpath=..').getByRole('button', { name, exact: true }).click());

/**
 * Открыть двойника из пункта `key`: «Открыть …» — действие пункта, а где действие пункта — удаление, сама строка.
 */
export const statusOpen = (page, key) => inStatusSheet(page, async sheet => {
  const row = sheet.getByTestId(`status-item-${key}`);
  const open = row.locator('xpath=..').getByRole('button', { name: /^Открыть/ });
  await (await open.count() ? open : row).click();
});

/** Banner над частями — худший пункт «Мешает работе». */
export const statusBanner = page => page.getByTestId('invoice-status-banner');
