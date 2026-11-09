// Удаление строки с сохранённой разноской (#1655, change `costs-invoice-line-delete-allocation`, [COST-7] [COST-12]) —
// живым браузером, задача 1.4: окно называет разноску до записи, отказ оставляет строку и разноску, подтверждение её
// удаляет — у черновика сразу, записью очереди (costs-invoice-screen-12-autosave, D-12.2). Строка без разноски — проверка 1а `invoice-lines-smoke` (снэкбар «Вернуть», D-13.2).
//
// Отдельным файлом: `invoice-lines-smoke` и так велик. Едет внутри него, с его страницей и помощниками.

/** Разноска строки на сервере — по `id`: что окно обещало сохранить или удалить, проверяется записью, а не экраном. */
async function savedParts(api, invoiceId, lineId) {
  const line = (await api('GET', `/costs/invoices/${invoiceId}`)).lines.find(l => l.id === lineId);
  return line ? line.allocation.parts.length : null;
}

export async function deleteAllocationChecks({ page, check, stamp, api, invoice, open, cell, saveLines }) {
  await check('строка с разноской: окно называет обе стройки, отказ оставляет строку, подтверждение удаляет разноску записью', async () => {
    const known = await api('GET', '/constructions');
    const site = async name => known.find(c => c.name === name) ?? api('POST', '/constructions', { name });
    const first = await site('Демо-стройка');
    const second = await site('Стройка удаления строки (прогон)');
    const created = await invoice(`СЧ-Р${stamp}`);
    const view = await api('PUT', `/costs/invoices/${created.id}/lines`, { lines: [
      { supplierText: `Кабель разнесённый ${stamp}`, unit: 'м', quantity: '100', price: '10' },
      { supplierText: 'Труба без разноски', unit: 'м', quantity: '10', price: '5' },
    ] });
    const lineId = view.lines[0].id;
    await api('PUT', `/costs/invoices/${created.id}/lines/${lineId}/allocation`, {
      parts: [{ construction: first.id, quantity: '40' }, { construction: second.id, quantity: '60' }],
    });
    await open(`СЧ-Р${stamp}`);

    const writes = [];
    const listen = r => { if (r.method() === 'PUT' && r.url().endsWith('/lines')) writes.push(r.url()); };
    page.on('request', listen);
    const bin = page.getByRole('button', { name: 'Удалить строку 1', exact: true });
    await bin.click();
    const dialog = page.getByRole('dialog', { name: 'Удалить строку 1?' });
    await dialog.waitFor({ timeout: 5_000 }).catch(() => { throw new Error('удаление разнесённой строки не спросило'); });
    const said = await dialog.innerText();
    for (const words of [first.name, second.name, 'Разнесена на 2 цели', '40 м', '60 м', 'После сохранения её не вернуть'])
      if (!said.includes(words)) throw new Error(`окно не называет «${words}»: ${said.replace(/\s+/g, ' ')}`);
    const focused = await page.evaluate(() => document.activeElement?.textContent ?? '');
    if (focused !== 'Отмена') throw new Error(`фокус в окне не на «Отмена», а на «${focused}»: второй Enter удалил бы строку`);

    // Отказ: строка, разноска и фокус на месте, записи не было.
    await dialog.getByRole('button', { name: 'Отмена' }).click();
    await dialog.waitFor({ state: 'detached', timeout: 5_000 });
    if (await cell('Наименование в счёте', 1).inputValue() !== `Кабель разнесённый ${stamp}`)
      throw new Error('после «Отмена» строки 1 нет на месте');
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Удалить строку 1', null, { timeout: 5_000 })
      .catch(() => { throw new Error('после «Отмена» фокус не вернулся на корзину строки'); });
    if (await savedParts(api, created.id, lineId) !== 2) throw new Error('после «Отмена» разноска строки изменилась');
    if (writes.length) throw new Error('вопрос и «Отмена» отправили строки');

    // Подтверждение: строка ушла без «Вернуть», фокус — в поле строки на её месте; запись — сразу, очередью (D-12.2).
    await bin.click();
    await dialog.getByRole('button', { name: 'Удалить строку 1', exact: true }).click();
    await dialog.waitFor({ state: 'detached', timeout: 5_000 });
    if (await cell('Наименование в счёте', 1).inputValue() !== 'Труба без разноски')
      throw new Error('подтверждённая строка осталась в таблице');
    if (await page.locator('[role=region][aria-label^="Notifications"] li').filter({ hasText: 'Строка 1 удалена.' }).count())
      throw new Error('у строки с разноской появилось «Вернуть»: разноску после записи не вернуть');
    const where = await page.evaluate(() => {
      const el = document.activeElement;
      return { cell: !!el?.closest('[data-cell]'), row: el?.closest('tr')?.getAttribute('data-row') ?? null };
    });
    if (!where.cell || where.row !== '0') throw new Error(`после удаления фокус не в поле строки на месте удалённой: ${JSON.stringify(where)}`);
    await saveLines();
    page.off('request', listen);
    if (writes.length !== 1) throw new Error(`подтверждение удаления дало записей строк: ${writes.length}, ждали одну`);
    const after = await api('GET', `/costs/invoices/${created.id}`);
    if (after.lines.some(l => l.id === lineId)) throw new Error('после подтверждения удалённая строка осталась в счёте');
    if (after.lines.length !== 1) throw new Error(`после подтверждения строк ${after.lines.length}, ждали одну`);
  });
}
