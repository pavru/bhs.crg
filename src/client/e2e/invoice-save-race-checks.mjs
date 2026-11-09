// Правка строк во время их записи (issue #1656, change `costs-invoice-lines-save-race`, ТЗ COST-7) — живым
// браузером: запись строк задержана, человек набирает в строках, пока она идёт. До правки ответ ложился на
// черновик целиком — набранное пропадало, — а свой ответ при грязном черновике читался чужой правкой: плашка
// «Счёт изменили» и 409 на следующей записи.
//
// Ломается: вернуть в `InvoiceLinesTable.save` пересборку черновика из ответа (`toDrafts(saved.lines)`) — набранное
// пропадёт; убрать `base.adopt(saved)` — вторая запись назовёт прежнюю версию; убрать `newId` из `toPayload` —
// ключ новой строки сменится ответом, и фокус уйдёт из клетки.
//
// Отдельным файлом: `invoice-lines-smoke` и так велик. Едет внутри него, с его помощниками.

import { statusPhrase } from './invoice-status.mjs';
import { until } from './harness.mjs';
import { commitInvoice, untilSaveState } from './invoice-save.mjs';

/** На сколько задержать запись строк — человек успевает набрать. */
const HOLD_MS = 1_500;

export async function saveRaceChecks({ page, check, stamp, invoice, open, cell, addRow, saveLines }) {
  await check('правка строк во время их записи не теряется; вторая запись — с версией ответа, без 409 и «Счёт изменили»', async () => {
    const number = `СЧ-Г${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('1');
    await cell('Цена', 1).fill('10');

    // Слушатель и перехват снимаются при ЛЮБОМ исходе (ревью PR #1811): страница общая на весь прогон, и
    // оставленный слушатель писал бы в `writes` чужие записи следующих проверок.
    const writes = [];
    const onResponse = r => {
      if (r.request().method() === 'PUT' && r.url().endsWith('/lines'))
        writes.push({ status: r.status(), seen: r.request().headers()['if-match'] });
    };
    let held = 0;
    const hold = async route => {
      held += 1;
      await new Promise(resolve => setTimeout(resolve, HOLD_MS));
      await route.continue();
    };
    page.on('response', onResponse);
    try {
      await page.route('**/api/costs/invoices/*/lines', hold);
      try {
        const isLines = r => r.method() === 'PUT' && r.url().endsWith('/lines');
        const sent = page.waitForRequest(isLines, { timeout: 15_000 });
        // Уход из строки — повод записи (D-12.2): первая запись уходит и задерживается.
        await commitInvoice(page);
        await sent;
        // Ожидание ответа — после отправки: ответ задержан на HOLD_MS и раньше не придёт, а заведённое до
        // `sent` ожидание при его отказе осталось бы непойманным отказом.
        const answered = page.waitForResponse(r => isLines(r.request()), { timeout: 15_000 });

        // Запись идёт: правка лежащей в снимке строки и новая строка.
        await cell('Количество', 1).fill('3');
        await addRow(2);
        await cell('Количество', 2).fill('5');
        await cell('Цена', 2).fill('2');
        await cell('Количество', 1).focus();

        const first = await answered;
        if (!first.ok()) throw new Error(`первая запись строк: ${first.status()}`);
        if (held !== 1) throw new Error(`запись строк задержана ${held} раз — набор шёл не во время записи`);
      } finally {
        await page.unroute('**/api/costs/invoices/*/lines', hold);
      }

      const one = await cell('Количество', 1).inputValue();
      const two = await cell('Количество', 2).inputValue();
      if (one !== '3' || two !== '5') throw new Error(`набранное во время записи пропало: строка 1 «${one}», строка 2 «${two}»`);
      // Строка под курсором не перемонтирована: её ключ — newId, ставший её id.
      const focused = await cell('Количество', 1).evaluate(el => el === document.activeElement);
      if (!focused) throw new Error('фокус ушёл из клетки «Количество, строка 1» после ответа записи');
      if (/Счёт изменили/.test(await statusPhrase(page))) throw new Error('свой ответ записи прочитан как чужая правка — «Счёт изменили»');

      // Набранное во время записи уходит СЛЕДУЮЩЕЙ записью очереди — само: из строк уходили, пока первая была в
      // пути (costs-invoice-screen-12-autosave, D-12.1). Её и ждём, а не нажатия.
      await until(() => { if (writes.length < 2) throw new Error(`записей строк ${writes.length}, следующая не ушла`); });
      await untilSaveState(page, 'saved');
      if (writes.length !== 2) throw new Error(`записей строк ${writes.length}, ожидалось 2`);
      if (writes.some(w => w.status === 409)) throw new Error(`запись получила 409: ${JSON.stringify(writes)}`);
      if (writes[0].seen === writes[1].seen) throw new Error(`вторая запись назвала ту же версию, что первая: ${writes[0].seen}`);
      if (/Счёт изменили/.test(await statusPhrase(page))) throw new Error('после второй записи — «Счёт изменили»');
    } finally {
      page.off('response', onResponse);
    }

    // Перечитанный счёт: обе строки с набранным, новая записана один раз — вторая запись назвала её `id`.
    await page.reload({ waitUntil: 'networkidle' });
    await open(number);
    const after = [await cell('Количество', 1).inputValue(), await cell('Количество', 2).inputValue()];
    if (after.join() !== '3,5') throw new Error(`после перезагрузки количества «${after.join(', ')}», ожидалось 3, 5`);
    if (await cell('Количество', 3).count()) throw new Error('строк больше двух — новая строка записана дважды');
  });
}
