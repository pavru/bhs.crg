// Выбор стройки под фильтром «Избранные стройки» (issue #1219) и рейл счетов под ним (#1221) —
// проверки живым браузером.
//
// Внутри `allocation-matrix-smoke`, а не своим набором: входы прогонов CI упёрлись в предел частоты
// (считает `suites.test.mjs`). Этот набор входит администратором и уже держит восемь своих строек.
//
// ⚠️ Избранное хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленное избранное
// урезало бы им выбор строек. Поэтому настройки сбрасываются в finally — адресом, а не через экран.

import { BASE } from './harness.mjs';

const RESET = { favoriteConstructions: null, favoritesFilter: null };
const REVEAL = /^Показать все стройки \(ещё \d+\)…$/;

/** Подписи групп выбора и их пункты — то, что видит человек, открыв список. */
const groupsOf = select => select.evaluate(s => Object.fromEntries(
  [...s.querySelectorAll('optgroup')].map(g => [g.label, [...g.children].map(o => o.textContent)])));
const optionTexts = select => select.evaluate(s => [...s.options].map(o => o.textContent));
const selectedText = select => select.evaluate(s => s.selectedOptions[0]?.textContent ?? '');

export async function sitePickerChecks({ page, check, api, invoice, openMatrix, sites, stamp }) {
  const [favorite, , , other] = sites;
  // Счёт, разнесённый на неизбранную стройку: в рейле его нет, открывается он ссылкой (#1221).
  let elsewhere = null;
  await api('PUT', '/account/settings', { favoriteConstructions: `${sites[0].id},${sites[1].id}`, favoritesFilter: null });
  try {
    await check('site-picker-reveal-keeps-what-was-typed', async () => {
      // «Показать все» посреди разноски: значение соседнего поля и набранное количество целы, раскрыта
      // вся форма, и ни одна запись не ушла — черновик с пустой целью на сервер не попадает.
      const number = `МТР-ИЗБ-${stamp}`;
      await invoice(number, [{ supplierText: 'Кабель под фильтром', unit: 'м', quantity: 10, price: 1 }], 10);
      const dialog = await openMatrix(page, number, 'на несколько объектов');
      const first = dialog.getByLabel('Объект 1', { exact: true });
      await first.selectOption({ label: favorite.name });
      await dialog.getByRole('button', { name: 'Добавить объект' }).click();
      const second = dialog.getByLabel('Объект 2', { exact: true });
      const cell = dialog.locator('tbody input').first();
      await cell.fill('4');

      // Выход — в поле, где значение УЖЕ стоит: на пустом поле сброс значения был бы незаметен.
      const offered = await optionTexts(first);
      if (offered.includes(other.name)) throw new Error('неизбранная стройка предложена без «Показать все»');
      const reveal = offered.find(text => REVEAL.test(text));
      if (!reveal) throw new Error(`выхода «Показать все стройки» нет: ${JSON.stringify(offered)}`);

      const writes = [];
      const onRequest = r => { if (r.method() !== 'GET' && r.url().includes('/costs/invoices/')) writes.push(r.url()); };
      page.on('request', onRequest);
      try {
        await first.selectOption({ label: reveal });
        const kept = await selectedText(first);
        if (kept !== favorite.name) throw new Error(`выход сменил значение поля: «${kept}» вместо «${favorite.name}»`);
        if ((await cell.inputValue()) !== '4') throw new Error(`набранное количество потеряно: «${await cell.inputValue()}»`);
        for (const [label, select] of [['первое', first], ['соседнее', second]]) {
          const groups = await groupsOf(select);
          if (!groups['Остальные стройки']?.includes(other.name))
            throw new Error(`${label} поле не раскрыто: ${JSON.stringify(Object.keys(groups))}`);
        }
        await second.selectOption({ label: other.name });
        if ((await cell.inputValue()) !== '4') throw new Error('после выбора неизбранной стройки количество потеряно');
        if (writes.length) throw new Error(`выход ушёл запросом на запись: ${writes.join(', ')}`);
      } finally {
        page.off('request', onRequest);
      }
    });

    await check('site-picker-non-favorite-current-is-plain', async () => {
      // Счёт, разнесённый раньше на неизбранную стройку: в поле её имя, группой «Не в избранном», без
      // слов о потере — живая стройка не «удалена».
      const number = `МТР-НИЗ-${stamp}`;
      const id = await invoice(number, [{ supplierText: 'Кабель на чужую стройку', unit: 'м', quantity: 5, price: 2 }], 10);
      const view = await api('GET', `/costs/invoices/${id}`);
      await api('PUT', `/costs/invoices/${id}/allocation`, {
        lines: [{ line: view.lines[0].id, parts: [{ construction: other.id, quantity: 5 }] }],
        document: [],
        stamp: view.allocation.stamp,
      });
      elsewhere = { id, number };
      // По ссылке: в рейле под фильтром этого счёта нет (#1221).
      await page.goto(`${BASE}/invoices?invoice=${id}`, { waitUntil: 'networkidle' });
      const select = page.getByLabel('Объект счёта', { exact: true });
      await select.waitFor({ timeout: 10_000 });
      const shown = await selectedText(select);
      if (shown !== other.name) throw new Error(`в поле «${shown}», а не «${other.name}»`);
      const groups = await groupsOf(select);
      if (JSON.stringify(groups['Не в избранном']) !== JSON.stringify([other.name]))
        throw new Error(`стоящая стройка не в группе «Не в избранном»: ${JSON.stringify(groups)}`);
    });

    await check('invoice-rail-hides-non-favorite-and-says-why', async () => {
      // Тот же счёт: строки в рейле нет, над формой — почему, у списка — сколько скрыто.
      if (!elsewhere) throw new Error('счёт на неизбранной стройке не заведён предыдущей проверкой');
      const rail = page.getByRole('button', { name: new RegExp(elsewhere.number) });
      if (await rail.count()) throw new Error('счёт неизбранной стройки стоит в рейле под фильтром');
      await page.getByTestId('invoice-not-in-rail').waitFor({ timeout: 5_000 });
      const note = await page.getByTestId('site-filter-note').innerText();
      if (!/скрыто: [1-9]/.test(note)) throw new Error(`строка у рейла: «${note}»`);
    });

    await check('invoice-rail-switch-keeps-open-invoice', async () => {
      // Переключение фильтра в шапке открытый счёт не закрывает: выбор живёт в адресе.
      const switcher = page.getByTestId('site-filter-switch');
      await switcher.getByRole('button', { name: /^Все/ }).click();
      await page.getByRole('button', { name: new RegExp(elsewhere.number) }).first().waitFor({ timeout: 5_000 });
      if (new URL(page.url()).searchParams.get('invoice') !== elsewhere.id) throw new Error('«Все» закрыло открытый счёт');
      if (await page.getByTestId('invoice-not-in-rail').count()) throw new Error('без фильтра форма говорит «нет в списке»');
      await switcher.getByRole('button', { name: /Избранные/ }).click();
      await page.getByTestId('invoice-not-in-rail').waitFor({ timeout: 5_000 });
      if (new URL(page.url()).searchParams.get('invoice') !== elsewhere.id) throw new Error('«Избранные» закрыли открытый счёт');
    });

    await check('invoice-rail-saved-to-non-favorite-toasts', async () => {
      // Неразнесённый счёт виден; разнесли на неизбранную стройку — тост говорит, куда он делся.
      const number = `МТР-ТСТ-${stamp}`;
      const id = await invoice(number, [{ supplierText: 'Кабель уходит из рейла', unit: 'м', quantity: 3, price: 2 }], 6);
      await page.goto(`${BASE}/invoices?invoice=${id}`, { waitUntil: 'networkidle' });
      await page.getByRole('button', { name: new RegExp(number) }).first().waitFor({ timeout: 10_000 });
      const select = page.getByLabel('Объект счёта', { exact: true });
      await select.selectOption({ label: (await optionTexts(select)).find(text => REVEAL.test(text)) });
      await select.selectOption({ label: other.name });
      // `.first()`: Radix Toast повторяет текст в живой области для экранного диктора (role="status"), и
      // на CI вторая копия успевает появиться к проверке — строгий локатор видел два элемента (CI PR #1334).
      await page.getByText('Сохранено. В списке счёт скрыт').first().waitFor({ timeout: 10_000 });
      await page.getByTestId('invoice-not-in-rail').waitFor({ timeout: 10_000 });
      if (await page.getByRole('button', { name: new RegExp(number) }).count())
        throw new Error('счёт, разнесённый на неизбранную стройку, остался в рейле');
    });

    await check('site-picker-report-reveal-stays-open', async () => {
      // Radix-список отчёта: выход раскрывает список НА МЕСТЕ. Закрой его Radix — раскрытое пришлось бы
      // открывать вторым щелчком, и выглядело бы это как «пункт не сработал» (ревью PR #1332).
      await page.goto(`${BASE}/site-costs`, { waitUntil: 'networkidle' });
      await page.getByRole('combobox', { name: 'Стройка' }).click();
      const list = page.getByRole('listbox');
      await list.waitFor({ timeout: 10_000 });
      if (await list.getByRole('option', { name: other.name, exact: true }).count())
        throw new Error('неизбранная стройка предложена без «Показать все»');
      await list.getByRole('option', { name: REVEAL }).click();
      await page.waitForTimeout(300);
      if (!(await list.isVisible())) throw new Error('после «Показать все» список закрылся');
      await list.getByText('Остальные стройки').waitFor({ timeout: 5_000 });
      await list.getByRole('option', { name: other.name, exact: true }).waitFor({ timeout: 5_000 });
      if (new URL(page.url()).searchParams.get('site')) throw new Error('выход сменил стройку отчёта');
      await page.keyboard.press('Escape');
    });

    await check('site-picker-waybill-of-non-favorite', async () => {
      const created = await api('POST', '/costs/waybills', { number: `РН-ИЗБ-${stamp}`, construction: other.id });
      try {
        await page.goto(`${BASE}/waybills?waybill=${created.id}`, { waitUntil: 'networkidle' });
        const select = page.getByLabel('Получатель — стройка', { exact: true });
        await select.waitFor({ timeout: 10_000 });
        const shown = await selectedText(select);
        if (shown !== other.name) throw new Error(`получатель показан как «${shown}», а не «${other.name}»`);
        if (await page.getByText('Стройки больше нет').count()) throw new Error('живая неизбранная стройка названа удалённой');
      } finally {
        await page.evaluate(async ([id, version]) => {
          const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
          await fetch(`/api/costs/waybills/${id}`, {
            method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': `"${version}"` },
          });
        }, [created.id, created.version]);
      }
    });
  } finally {
    await api('PUT', '/account/settings', RESET);
  }
}
