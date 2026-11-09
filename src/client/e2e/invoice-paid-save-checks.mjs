// Оплаченный черновик открытого периода пишется ЯВНО (costs-invoice-screen-12-autosave, D-12.5, задача 4.3,
// ТЗ COST-6.5): автосохранения нет — промежуточный итог получил бы отказ «оплаченный обязан оставаться сведённым».
// Запись — «Сохранить» в части «Сроки и оплата»; носитель сводки говорит «Не сохранено» и ведёт туда. Отказ сервера —
// его словами, набранное остаётся на месте.
//
// Отдельным файлом: `invoice-payment-smoke` и так велик. Едет внутри него, с его страницей и помощниками.

import { settled } from './harness.mjs';

export async function paidSaveChecks({ page, check, stamp, api, invoice, open, paidOn }) {
  await check('paid-draft-writes-only-by-save', async () => {
    const number = `ОПЛ-С-${stamp}`;
    const id = await invoice(number, 400, 400);
    const seen = await api('POST', `/costs/invoices/${id}/paid/preview`, { paidOn });
    await api('POST', `/costs/invoices/${id}/paid`, { paidOn, document: 'п/п прогона, явная запись', seen: seen.stamp });
    await open(number);

    const writes = [];
    const listen = r => {
      if (r.method() === 'PUT' && r.url().includes(`/costs/invoices/${id}`)) writes.push(r.url());
    };
    page.on('request', listen);
    try {
      const total = page.getByLabel('Сумма к оплате', { exact: true }).first();
      await total.fill('450');
      // Уход с поля у черновика — повод записи (D-12.2); у оплаченного — нет.
      await page.getByTestId('invoice-header').focus();
      await settled(page);
      await page.waitForTimeout(500);
      if (writes.length) throw new Error(`правка суммы оплаченного черновика ушла записью сама: ${writes.join(', ')}`);

      const carrier = page.getByTestId('invoice-save-state').first();
      if ((await carrier.getAttribute('data-state')) !== 'unsaved')
        throw new Error(`носитель после правки говорит «${(await carrier.innerText()).trim()}», а не «Не сохранено»`);
      const go = page.getByRole('button', { name: /«Сохранить» в «Сроках и оплате»/ }).first();
      await go.click();
      const save = page.getByRole('button', { name: 'Сохранить', exact: true });
      await save.waitFor({ timeout: 5_000 })
        .catch(() => { throw new Error('носитель не привёл к «Сохранить» в «Сроках и оплате»'); });

      // Итог 450 при строках на 400 — несведённый оплаченный: сервер отказывает, и отказ назван его словами.
      const answered = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes(`/costs/invoices/${id}`),
        { timeout: 15_000 });
      await save.click();
      const answer = await answered;
      if (answer.ok()) throw new Error('несведённый итог оплаченного счёта записан — сервер обязан был отказать');
      const said = await answer.json().catch(() => ({}));
      await settled(page);
      if ((await carrier.getAttribute('data-state')) !== 'refused')
        throw new Error(`после отказа носитель говорит «${(await carrier.innerText()).trim()}»`);
      const words = said.error ?? said.detail;
      if (words && !(await carrier.innerText()).includes(words.slice(0, 30)))
        throw new Error(`носитель не назвал отказ словами сервера «${words}»: «${(await carrier.innerText()).trim()}»`);
      if (!(await total.inputValue()).startsWith('450'))
        throw new Error(`после отказа набранное пропало: в поле «${await total.inputValue()}»`);

      // Сведённый итог записывается той же кнопкой — и носитель снова «Сохранено».
      await total.fill('400');
      await page.getByTestId('invoice-header').focus();
      const saved = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes(`/costs/invoices/${id}`),
        { timeout: 15_000 });
      await save.click();
      if (!(await saved).ok()) throw new Error('сведённый итог оплаченного счёта не записался');
      await settled(page);
      if ((await carrier.getAttribute('data-state')) !== 'saved')
        throw new Error(`после записи носитель говорит «${(await carrier.innerText()).trim()}»`);
      const view = await api('GET', `/costs/invoices/${id}`);
      if (Number(view.requisites['Итого']) !== 400) throw new Error(`на сервере итог ${view.requisites['Итого']}, а не 400`);
    } finally {
      page.off('request', listen);
    }
  });
}
