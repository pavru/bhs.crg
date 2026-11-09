// Поставщик в шапке изменён и не сохранён (issue #1657, change `costs-invoice-lines-unsaved-supplier`, ТЗ COST-7.1) —
// живым браузером. До правки строка, набранная после смены поставщика, получала позицию из памяти ПРЕЖНЕГО,
// с пометкой «запомнено», и чей это опыт, сказано не было. Выбор же, сделанный руками, обязан запомниться за
// НОВЫМ: очередь записи пишет шапку раньше строк (D-10.7, 12 D-12.1), и за прежним поставщиком не должно появиться ничего.
//
// Ломается: убрать `supplierPending` из `offerFor`/`suggest` в `InvoiceLinesTable` — строка получит позицию
// прежнего поставщика; переставить шаги в `saveInOrder` (строки раньше шапки) — выбор запомнится за прежним.
//
// Отдельным файлом: `invoice-lines-smoke` и так велик. Едет внутри него, с его помощниками.

import { untilStatus } from './invoice-status.mjs';

export async function unsavedSupplierChecks({ page, check, stamp, api, invoice, open, cell, addRow, saveLines, cable, organizations }) {
  await check('смена поставщика не сохранена: память прежнего не подставляется, выбор запоминается за новым', async () => {
    if (organizations.length < 2) throw new Error('в справочнике одна организация — сменить поставщика не на кого');
    const [before, after] = organizations;
    const afterName = after.name ?? after.displayName;
    const text = `Строка смены поставщика ${stamp}`;
    const matches = async () => (await api('GET', `/costs/supplier-matches?query=${encodeURIComponent(text)}`)).items;

    // За прежним поставщиком строка уже запомнена — её опыт и не должен попасть в счёт после смены.
    const known = await invoice(`СЧ-П${stamp}`);
    await api('PUT', `/costs/invoices/${known.id}/lines`, { lines: [{ supplierText: text, quantity: '1', price: '10' }] });
    await open(`СЧ-П${stamp}`);
    await page.getByRole('button', { name: 'выбрать позицию' }).first().click();
    await page.getByPlaceholder('часть наименования').fill(stamp);
    await page.getByRole('option', { name: cable }).click();
    await saveLines();
    const remembered = await matches();
    if (remembered.length !== 1 || remembered[0].supplierId !== before.id)
      throw new Error(`за прежним поставщиком не запомнилось: ${JSON.stringify(remembered)}`);

    // Новый счёт того же поставщика: в шапке — другой. Выбор организации у черновика пишется сразу (12, D-12.2),
    // поэтому «смена не сохранена» — это запись реквизитов в пути: её держим перехватом, пока проверяем строки.
    const number = `СЧ-П${stamp}-2`;
    const created = await invoice(number);
    await open(number);
    const header = `**/api/costs/invoices/${created.id}`;
    let release;
    const released = new Promise(resolve => { release = resolve; });
    await page.route(header, async route => {
      if (route.request().method() === 'PUT') await released;
      await route.continue();
    });
    const order = [];
    const onRequest = r => {
      if (r.method() === 'PUT' && r.url().includes(`/costs/invoices/${created.id}`)) order.push(r.url().endsWith('/lines') ? 'строки' : 'шапка');
    };
    page.on('request', onRequest);
    try {
      await page.getByLabel('Поставщик').click();
      await page.getByRole('option', { name: afterName, exact: true }).click();

      await addRow(1);
      await cell('Наименование в счёте', 1).fill(text);
      await cell('Количество', 1).fill('1');
      await cell('Цена', 1).fill('10');
      const lines = page.getByRole('table', { name: 'Строки счёта' });
      // Ответ на вопрос о запомненном пришёл бы за долю секунды — ждём заведомо дольше.
      await page.waitForTimeout(1_500);
      if (await lines.getByRole('button', { name: 'запомнено' }).count())
        throw new Error('строка получила позицию из памяти прежнего поставщика, хотя в шапке уже другой');
      await untilStatus(page, items => items.some(i => i.key === 'matches-pending'),
        'перечень не назвал, почему запомненное не подставлено');

      // Выбор руками — пометка говорит, за кем он запомнится.
      await page.getByRole('button', { name: 'выбрать позицию' }).first().click();
      await page.getByPlaceholder('часть наименования').fill(stamp);
      await page.getByRole('option', { name: cable }).click();
      await lines.getByRole('button', { name: 'запомнится' }).click();
      await page.getByText(/выбор запомнится за новым/).waitFor({ timeout: 5_000 })
        .catch(() => { throw new Error('пометка «запомнится» не сказала, что выбор уйдёт за поставщиком из шапки'); });
      await page.keyboard.press('Escape');

      // Запись реквизитов дошла — очередь пишет строки следом: шапка раньше строк, выбор — за новым поставщиком.
      release();
      await saveLines();
    } finally {
      release();
      page.off('request', onRequest);
      await page.unroute(header);
    }
    if (order.join(',') !== 'шапка,строки') throw new Error(`запись шла не шапкой раньше строк: ${order.join(', ')}`);

    const now = await matches();
    const of = id => now.filter(m => m.supplierId === id);
    if (of(after.id).length !== 1) throw new Error(`за новым поставщиком выбор не запомнился: ${JSON.stringify(now)}`);
    if (of(before.id).length !== 1 || of(before.id)[0].id !== remembered[0].id || of(before.id)[0].version !== remembered[0].version)
      throw new Error(`за прежним поставщиком память изменилась: было ${JSON.stringify(remembered)}, стало ${JSON.stringify(of(before.id))}`);
  });
}
