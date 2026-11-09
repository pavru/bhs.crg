// Запись формы счёта в живых прогонах (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5).
//
// Черновик пишется САМ — по уходу с поля и из строки; кнопка «Сохранить» осталась только там, где автосохранения
// нет (разобранный, оплаченный черновик, D-12.3). Прогону, которому нужно «записать набранное», не важно, какая у
// счёта модель: помощник делает то же, что человек, — уходит с поля либо жмёт кнопку.

import { settled } from './harness.mjs';

/** Форма счёта открылась: сводка на месте. Раньше признаком служила кнопка «Сохранить» — у черновика её нет. */
export async function invoiceOpened(page, { timeout = 10_000 } = {}) {
  await page.getByTestId('invoice-header').waitFor({ timeout });
}

/**
 * Записать набранное: у явной записи — «Сохранить», у черновика — уход с поля на сводку (она вне частей формы, и
 * фокус на ней — уход и с поля, и из строки). Ответа НЕ ждёт: ждёт его вызывающий, по своему запросу.
 */
export async function commitInvoice(page) {
  const button = page.getByRole('button', { name: 'Сохранить', exact: true });
  if ((await button.count()) > 0 && await button.first().isEnabled()) { await button.first().click(); return; }
  await page.getByTestId('invoice-header').focus();
}

/**
 * Записать набранное и дождаться, пока запись ДОШЛА: уйти с поля (или «Сохранить»), дождаться тишины сети и спросить
 * носитель. ⚠️ Ответа своего запроса не ждёт нарочно: у черновика выбор позиции, организации, вставка из буфера
 * пишутся сами, ещё до этого вызова, — ожидание ответа после них не дождалось бы ничего. Отказ — словами носителя.
 */
export async function saveInvoice(page) {
  await commitInvoice(page);
  await settled(page);
  const state = await saveState(page);
  if (state !== null && state !== 'saved')
    throw new Error(`запись не дошла — носитель говорит «${(await page.getByTestId('invoice-save-state').first().innerText()).replace(/\s+/g, ' ')}»`);
}

/** Состояние носителя записи в сводке: `saved`, `saving`, `failed`, `refused`; `null` — носителя нет. */
export async function saveState(page) {
  const carrier = page.getByTestId('invoice-save-state');
  return (await carrier.count()) > 0 ? carrier.first().getAttribute('data-state') : null;
}

/** Дождаться, пока носитель скажет `state` (по умолчанию — «Сохранено»). */
export async function untilSaveState(page, state = 'saved', { timeout = 15_000 } = {}) {
  const deadline = Date.now() + timeout;
  let seen = null;
  while (Date.now() < deadline) {
    seen = await saveState(page);
    if (seen === state) return;
    await page.waitForTimeout(50);
  }
  throw new Error(`носитель записи не сказал «${state}» за ${timeout / 1000} с — он говорит «${seen}»`);
}

/**
 * Подтвердить смену режима НДС в окне предпросмотра (D-12.6): у черновика флажок и подсказка режима открывают окно,
 * и режим меняется только по «Пересчитать…» («Сменить режим», когда пересчитывать нечего).
 * @returns текст предпросмотра — что окно обещало.
 */
export async function confirmVatMode(page) {
  const preview = page.getByTestId('vat-mode-preview');
  await preview.waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('смена режима НДС не показала предпросмотр пересчёта'); });
  const said = (await preview.innerText()).replace(/\s+/g, ' ');
  await page.getByRole('button', { name: /^(Пересчитать \d+ строк|Сменить режим)/ }).click();
  await preview.waitFor({ state: 'detached', timeout: 10_000 });
  return said;
}
