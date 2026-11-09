// Остаток разноски — сам во второй и следующий объект (#1818, п. 5б; change `costs-allocation-remainder-fill`,
// [COST-12.1] [COST-10] [COST-11]) — живым браузером, задачи 1.2 и 2.2: новая колонка матрицы и новая часть окна
// строки приходят с остатком, первая — пустой; подставленное правится и пишется ТОЛЬКО «Сохранить разноску».
//
// ⚠️ Что записано, проверяется сервером (`GET` счёта), а не экраном: экран показывал бы и то, чего нет в базе.
//
// Отдельным файлом: `allocation-matrix-smoke` и так велик. Едет внутри него, с его страницей и помощниками.

import { BASE, expandInvoiceParts, pickOption } from './harness.mjs';

/** Части разноски строк счёта на сервере: строка → количества или суммы частей. */
async function savedParts(api, id) {
  return (await api('GET', `/costs/invoices/${id}`)).lines
    .map(line => line.allocation.parts.map(p => p.quantity ?? p.amount));
}

/** Выход без записи: окно с набранным обязано спросить — как при ручном вводе. */
async function leaveUnsaved(dialog) {
  await dialog.locator('[data-modal-exit]').first().click();
  const question = dialog.page().getByRole('alertdialog', { name: 'Закрыть без сохранения?' });
  await question.waitFor({ timeout: 5_000 })
    .catch(() => { throw new Error('окно с подставленным остатком закрылось без вопроса'); });
  await question.getByRole('button', { name: 'Закрыть', exact: true }).click();
  await dialog.waitFor({ state: 'hidden', timeout: 5_000 });
}

export async function remainderChecks({ page, check, api, invoice, openMatrix, sites, stamp }) {
  const writes = [];
  const listen = r => { if (r.method() === 'PUT' && r.url().includes('/allocation')) writes.push(r.url()); };

  await check('remainder-fills-next-object', async () => {
    const number = `МТР-ОСТ-${stamp}`;
    const id = await invoice(number, [
      { supplierText: 'Кабель остатка', unit: 'м', quantity: 30, price: 140 },
      { supplierText: 'Доставка остатка', amount: 1500 },
    ], 5700);

    // Значения полей клеток построчно: [объект 1, объект 2]. Поля шапки (выбор, процент) — в thead.
    const values = dialog => dialog.locator('tbody tr').evaluateAll(rows =>
      rows.map(row => [...row.querySelectorAll('input')].map(input => input.value)));
    const rests = dialog => dialog.locator('tbody tr').evaluateAll(rows =>
      rows.map(row => [...row.querySelectorAll('td')].at(-1).innerText.trim()));

    /** Открыть матрицу неразнесённого счёта, набрать 20 м кабеля на объект 1 и добавить объект 2. */
    async function secondObject() {
      const dialog = await openMatrix(page, number, 'на несколько объектов');
      const opened = await values(dialog);
      if (JSON.stringify(opened) !== JSON.stringify([[''], ['']]))
        throw new Error(`первый объект не пустой: ${JSON.stringify(opened)}`);
      await pickOption(dialog.getByLabel('Объект 1', { exact: true }), sites[0].name);
      await dialog.locator('tbody tr').nth(0).locator('input').first().fill('20');
      await dialog.getByRole('button', { name: 'Добавить объект' }).click();
      return dialog;
    }

    page.on('request', listen);
    try {
      // 1. Подставлено ровно «Не разнесено»: 10 м кабеля и вся доставка, остаток — 0. Ничего не записано.
      let dialog = await secondObject();
      const filled = await values(dialog);
      if (JSON.stringify(filled) !== JSON.stringify([['20', '10'], ['', '1500']]))
        throw new Error(`объект 2 пришёл не с остатком строк: ${JSON.stringify(filled)}`);
      const zero = await rests(dialog);
      if (!zero.every(text => /^≈\s*0(\D|$)/.test(text)))
        throw new Error(`«Не разнесено» после подстановки не 0: ${zero.join(' | ')}`);
      await leaveUnsaved(dialog);
      if (writes.length) throw new Error(`подстановка ушла запросом на запись: ${writes.join(', ')}`);
      if (JSON.stringify(await savedParts(api, id)) !== '[[],[]]') throw new Error('разноска счёта изменилась без «Сохранить разноску»');

      // 2. Подставленное правится как набранное: 10 → 6 даёт остаток 4 м, и записывается 6.
      dialog = await secondObject();
      const second = dialog.locator('tbody tr').nth(0).locator('input').nth(1);
      await second.fill('6');
      const four = (await rests(dialog))[0];
      if (!/^≈\s*4\s*м/.test(four)) throw new Error(`после правки 10 → 6 «Не разнесено» — «${four}», а не «≈ 4 м»`);
      await pickOption(dialog.getByLabel('Объект 2', { exact: true }), sites[1].name);
      await Promise.all([
        page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${id}/allocation`) && r.request().method() === 'PUT'),
        dialog.getByRole('button', { name: 'Сохранить разноску' }).click(),
      ]);
      const saved = await savedParts(api, id);
      if (JSON.stringify(saved) !== JSON.stringify([[20, 6], [1500]]))
        throw new Error(`записано не набранное: ${JSON.stringify(saved)}`);
      await dialog.locator('[data-modal-exit]').first().click();
      await dialog.waitFor({ state: 'hidden', timeout: 5_000 });
    } finally {
      page.off('request', listen);
    }
  });

  await check('remainder-fills-next-part', async () => {
    const number = `МТР-ОСТ-Ч-${stamp}`;
    const id = await invoice(number, [{ supplierText: 'Кабель части', unit: 'м', quantity: 30, price: 140 }], 4200);
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: new RegExp(number) }).first().click();
    await expandInvoiceParts(page);
    await page.getByRole('button', { name: 'Разноска, строка 1' }).click();
    const dialog = page.getByRole('dialog', { name: /^Разноска строки 1/ });
    await dialog.waitFor({ timeout: 10_000 });

    writes.length = 0;
    page.on('request', listen);
    try {
      const first = dialog.getByLabel('Количество, часть 1', { exact: true });
      if ((await first.inputValue()) !== '') throw new Error(`первая часть не пустая: «${await first.inputValue()}»`);
      await first.fill('20');
      await dialog.getByRole('button', { name: 'Добавить часть' }).click();
      const second = await dialog.getByLabel('Количество, часть 2', { exact: true }).inputValue();
      if (second !== '10') throw new Error(`вторая часть «Кабель, 30 м» после 20 м — «${second}», а не 10`);
      await leaveUnsaved(dialog);
      if (writes.length) throw new Error(`подстановка ушла запросом на запись: ${writes.join(', ')}`);
      if (JSON.stringify(await savedParts(api, id)) !== '[[]]') throw new Error('разноска строки изменилась без «Сохранить разноску»');
    } finally {
      page.off('request', listen);
    }
  });
}
