// Отмена вместо подтверждения (costs-invoice-screen-13-undo, ТЗ COST-6.6) — живым браузером: отложенное
// удаление нового черновика (задачи 3.3 и 3.4). Отклонение с «Вернуть» — в проверке 11 `invoices-smoke` и в
// очереди `invoice-lines-smoke`, удаление строки — в `invoice-lines-smoke`.
//
// Отдельным файлом: `invoices-smoke` у предела размера. Едет внутри него, с его страницей и помощниками.
//
// ⚠️ Файл нарочно нечитаем (как в проверке 7 `invoices-smoke`): исход `BadFile` без запроса к движку (#1344) —
// и «Не тот файл — удалить черновик» на любом стенде.

import { statusBanner } from './invoice-status.mjs';

/** Срок «Вернуть» (`SNACKBAR_UNDO_MS`) и запас на уход уведомления. */
const UNDO_MS = 8_000;
const SETTLE_MS = UNDO_MS + 4_000;

export async function invoiceUndoChecks(page, check, h) {
  const { named, stamp, BASE } = h;
  const status = id => page.evaluate(async id => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    return (await fetch(`/api/costs/invoices/${id}`, { headers: { Authorization: `Bearer ${token}` } })).status;
  }, id);
  const deletes = [];
  page.on('request', r => { if (r.method() === 'DELETE' && /\/costs\/invoices\/[^/]+$/.test(r.url())) deletes.push(r.url()); });

  /** «Счёт из файла» с нечитаемым файлом: черновик этой вкладки — новый, без правок. */
  async function fromBadFile(name) {
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.locator('input[type=file][multiple][accept*=".pdf"]').setInputFiles({
      name, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${name}`),
    });
    await named('черновик из файла не открылся', () => page.waitForURL(/[?&]invoice=/, { timeout: 15_000 }));
    const id = new URL(page.url()).searchParams.get('invoice');
    await named('banner сбоя распознавания не появился', () => statusBanner(page).waitFor({ timeout: 30_000 }));
    return id;
  }
  const snackbar = () => page.locator('[role=region][aria-label^="Notifications"] li').filter({ hasText: 'Черновик удалён' });

  await check('«Не тот файл — удалить» у нового черновика: окна нет, «Вернуть» возвращает, закрытие снэкбара удаляет', async () => {
    const id = await fromBadFile(`Не-тот-${stamp}.pdf`);
    const button = statusBanner(page).getByRole('button', { name: 'Не тот файл — удалить черновик', exact: true });
    await named('у banner нет «Не тот файл — удалить черновик» без многоточия', () => button.waitFor({ timeout: 10_000 }));
    deletes.length = 0;
    await button.click();
    if (await page.getByRole('dialog').count()) throw new Error('удаление нового черновика открыло окно');
    await named('снэкбара «Черновик удалён · Вернуть» нет', () => snackbar().waitFor({ timeout: 5_000 }));
    if (page.url().includes(`invoice=${id}`)) throw new Error('удалённый черновик остался открытым');

    // «Вернуть» — запроса не было, черновик снова открыт.
    await snackbar().getByRole('button', { name: 'Вернуть' }).click();
    await named('«Вернуть» не открыл черновик', () => page.waitForURL(new RegExp(`invoice=${id}`), { timeout: 10_000 }));
    await page.waitForTimeout(SETTLE_MS);
    if (deletes.length) throw new Error(`после «Вернуть» ушёл запрос удаления: ${deletes.join(', ')}`);
    if (await status(id) !== 200) throw new Error('после «Вернуть» черновика нет на сервере');

    // Снова удалить — и дождаться закрытия снэкбара: запрос уходит, счёта больше нет.
    await statusBanner(page).getByRole('button', { name: 'Не тот файл — удалить черновик', exact: true }).click();
    await snackbar().waitFor({ timeout: 5_000 });
    await named('снэкбар не закрылся сам', () => snackbar().waitFor({ state: 'detached', timeout: SETTLE_MS }));
    await named('после закрытия снэкбара черновик остался на сервере', () => page.waitForFunction(
      async id => (await fetch(`/api/costs/invoices/${id}`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token')}` },
      })).status === 404, id, { timeout: 10_000, polling: 500 }));
    if (deletes.length !== 1) throw new Error(`запросов удаления ${deletes.length}, ждали один`);
  });

  await check('уход по рейлу удаление не отменяет: после закрытия снэкбара черновика нет', async () => {
    const id = await fromBadFile(`Рейл-${stamp}.pdf`);
    await statusBanner(page).getByRole('button', { name: 'Не тот файл — удалить черновик', exact: true }).click();
    await snackbar().waitFor({ timeout: 5_000 });
    await page.getByRole('link', { name: 'Накладные' }).first().click();
    await named('со страницы счетов не ушли', () => page.waitForURL(/\/waybills/, { timeout: 10_000 }));
    await page.waitForTimeout(SETTLE_MS);
    if (await status(id) !== 404) throw new Error('черновик, удалённый перед уходом по рейлу, остался на сервере');
  });
}
