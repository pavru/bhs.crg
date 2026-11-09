// Разобранный счёт правится режимом «Изменить» (costs-invoice-screen-12-autosave, D-12.4, задача 4.2, ТЗ COST-6.5):
// до «Изменить» поля для чтения; «Отменить правку» возвращает записанное без записи; «Разобран» записывает правку
// (реквизиты → строки) и подтверждает переход. Правка, ломающая готовность, опускает счёт в черновик — сервер делает это
// сам, а форма показывает, чего не хватает.
//
// Отдельным файлом: `invoice-lines-smoke` и так велик. Едет внутри него, с его страницей и помощниками.

import { settled } from './harness.mjs';
import { untilStatus } from './invoice-status.mjs';

/** Запрос с версией счёта заголовком `If-Match`: переходы состояния её требуют. */
async function transition(page, id, path) {
  return page.evaluate(async ([id, path]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const auth = { Authorization: `Bearer ${token}` };
    const seen = await (await fetch(`/api/costs/invoices/${id}`, { headers: auth })).json();
    const res = await fetch(`/api/costs/invoices/${id}/${path}`, {
      method: 'POST', headers: { ...auth, 'Content-Type': 'application/json', 'If-Match': `"${seen.version}"` },
    });
    if (!res.ok) throw new Error(`${path} → ${res.status} ${(await res.text()).slice(0, 200)}`);
  }, [id, path]);
}

export async function editModeChecks({ page, check, stamp, api, invoice, open, cell, addRow, position }) {
  const site = (await api('GET', '/constructions')).find(c => c.name === 'Демо-стройка');
  const positionId = await position(`Кабель режима «Изменить» ${stamp}`);

  /** Разобранный счёт: строка с позицией, разнесённая на стройку целиком, сумма к оплате сходится со строками. */
  async function parsedInvoice(number) {
    const created = await invoice(number, 1_000);
    const view = await api('PUT', `/costs/invoices/${created.id}/lines`, { lines: [{
      nomenclature: { $ref: 'catalog', entryId: positionId }, supplierText: 'Кабель разобранного', unit: 'м',
      quantity: '2', price: '500',
    }] });
    await api('PUT', `/costs/invoices/${created.id}/lines/${view.lines[0].id}/allocation`, {
      parts: [{ construction: site.id, quantity: '2' }],
    });
    await transition(page, created.id, 'parsed');
    return created.id;
  }

  const writes = [];
  const listen = r => { if (r.method() === 'PUT' && r.url().includes('/costs/invoices/')) writes.push(r.url()); };
  const total = () => page.getByLabel('Сумма к оплате', { exact: true }).first();
  const editable = async () => (await total().getAttribute('readonly')) === null;

  await check('разобранный: поля для чтения до «Изменить»; «Отменить правку» без записи; правка суммы и «Разобран» — счёт разобран', async () => {
    if (!site) throw new Error('стройки «Демо-стройка» нет — посев не отработал');
    const number = `СЧ-И${stamp}`;
    const id = await parsedInvoice(number);
    await open(number);
    if (await editable()) throw new Error('у разобранного счёта «Сумма к оплате» правится до «Изменить»');

    page.on('request', listen);
    try {
      await page.getByRole('button', { name: 'Изменить', exact: true }).click();
      if (!(await editable())) throw new Error('после «Изменить» «Сумма к оплате» по-прежнему для чтения');
      await total().fill('999');
      await page.getByTestId('invoice-header').focus();
      await settled(page);
      if (writes.length) throw new Error(`правка в режиме «Изменить» ушла записью сама: ${writes.join(', ')}`);
      await page.getByRole('button', { name: 'Отменить правку', exact: true }).click();
      if (!(await total().inputValue()).startsWith('1'))
        throw new Error(`«Отменить правку» не вернула записанное: в поле «${await total().inputValue()}»`);
      if (await editable()) throw new Error('после «Отменить правку» поля остались открыты');
      if (writes.length) throw new Error(`«Отменить правку» записала: ${writes.join(', ')}`);

      // Настоящая правка: сумма к оплате и цена строки — вместе, иначе переход отказал бы за расхождение.
      await page.getByRole('button', { name: 'Изменить', exact: true }).click();
      await total().fill('1200');
      await cell('Цена', 1).fill('600');
      await page.getByTestId('invoice-header').focus();
      const parsed = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith(`/${id}/parsed`),
        { timeout: 20_000 });
      await page.getByRole('button', { name: 'Разобран', exact: true }).click();
      const answer = await parsed;
      if (!answer.ok()) throw new Error(`«Разобран» после правки отказал: ${answer.status()} ${(await answer.text()).slice(0, 300)}; записи: ${writes.join(', ')}`);
      await settled(page);
      const view = await api('GET', `/costs/invoices/${id}`);
      if (Number(view.requisites['Итого']) !== 1200) throw new Error(`на сервере итог ${view.requisites['Итого']}, а не 1200`);
      if (Number(view.lines[0]?.price) !== 600) throw new Error(`на сервере цена строки ${view.lines[0]?.price}, а не 600`);
      if (view.requisites['Состояние'] !== 'Разобран') throw new Error(`после «Разобран» счёт — «${view.requisites['Состояние']}»`);
      if (await editable()) throw new Error('после «Разобран» поля остались открыты: режим «Изменить» не закрылся');
    } finally {
      page.off('request', listen);
      writes.length = 0;
    }
  });

  await check('разобранный: строка без позиции в «Изменить» и «Разобран» — черновик с пунктом готовности', async () => {
    const number = `СЧ-Ч${stamp}`;
    const id = await parsedInvoice(number);
    await open(number);
    await page.getByRole('button', { name: 'Изменить', exact: true }).click();
    const rows = await page.getByLabel(/^Количество, строка \d+$/).count();
    await addRow(rows + 1);
    await cell('Наименование в счёте', rows + 1).fill('Строка без позиции');
    await cell('Количество', rows + 1).fill('1');
    await cell('Цена', rows + 1).fill('10');
    await page.getByTestId('invoice-header').focus();
    await page.getByRole('button', { name: 'Разобран', exact: true }).click();
    await settled(page);
    await page.getByTestId('invoice-header').getByText(/^Черновик/).first().waitFor({ timeout: 15_000 })
      .catch(() => { throw new Error('правка, сломавшая готовность, не опустила счёт в черновик'); });
    await untilStatus(page, items => items.some(i => i.key.startsWith('gap:nomenclature')),
      'у опущенного в черновик счёта индикатор не назвал строку без позиции');
    const view = await api('GET', `/costs/invoices/${id}`);
    if (view.lines.length !== rows + 1) throw new Error(`на сервере строк ${view.lines.length}, а набрано ${rows + 1}`);
    // Черновик пишется сам (D-12.3): режим закрылся, поля открыты.
    if (!(await editable())) throw new Error('у опущенного в черновик счёта поля остались для чтения');
  });
}
