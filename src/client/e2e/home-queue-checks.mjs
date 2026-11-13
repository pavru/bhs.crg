// Главная → «Разобрать» (shell-08, ТЗ UI-34) — живым браузером: карточка очереди с числом ведёт в список
// счетов под этим отбором, и число карточки то же, что у чипа экрана (у них общий счётчик сервера).
//
// Отдельным файлом: `invoices-smoke` у предела размера. Едет внутри него, после проверки 1, где заведён
// черновик: он и стоит в очереди «Разобрать», так что карточка на любом стенде есть. Он же открыт на экране —
// и «Недавние» (ТЗ UI-35) обязаны показать его первым и на другом устройстве.

import { pickQueue, queueChip, queueChipCount, queueOptionCount, settled, watchRequests } from './harness.mjs';

export async function homeQueueChecks(page, check, { BASE, browser }) {
  await check('Недавние: открытый счёт первым на другом устройстве', async () => {
    const opened = new URL(page.url()).searchParams.get('invoice');
    if (!opened) throw new Error(`черновик не открыт на экране: ${page.url()}`);
    // Отметка «открыто» уходит без ожидания ответа: пока её POST в полёте, другое устройство прочло бы прежний
    // список — проверка мигала бы под нагрузкой. Затихшая страница — отметка записана.
    await settled(page);
    // Другое устройство — своё окно браузера со своим хранилищем; вход тот же (токен переносится), иначе
    // лишний вход занял бы место в счёте входов прогона (`suites.test.mjs`).
    const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
    const ctx = await browser.newContext({ viewport: { width: 1366, height: 768 }, locale: 'ru-RU' });
    await ctx.addInitScript(t => localStorage.setItem('access_token', t), token);
    const other = await ctx.newPage();
    try {
      await watchRequests(other);
      await other.goto(`${BASE}/home`);
      await settled(other);
      const first = other.getByTestId('home-recent-row').first();
      await first.waitFor({ timeout: 10_000 });
      const href = await first.getAttribute('href');
      if (new URL(href, BASE).searchParams.get('invoice') !== opened)
        throw new Error(`первой в «Недавних» стоит «${href}», а открыт счёт ${opened}`);
    } finally {
      await ctx.close();
    }
  });

  await check('Главная: карточка «Разобрать» открывает список счетов под отбором «Разобрать»', async () => {
    await page.goto(`${BASE}/home`, { waitUntil: 'networkidle' });
    const card = page.getByRole('link', { name: 'Разобрать', exact: true });
    await card.waitFor({ timeout: 10_000 });
    const number = (await page.getByTestId('home-queue-costs-parsing').locator('.text-headline-small').innerText()).trim();
    await card.click();
    await page.waitForURL(u => u.pathname === '/invoices' && u.searchParams.get('queue') === 'parsing', { timeout: 10_000 });
    // Очереди — один чип с меню (costs-invoice-filter-row): выбранная называется на нём со своим числом.
    const chip = queueChip(page);
    await chip.waitFor({ timeout: 10_000 });
    const chosen = async () => (await chip.getAttribute('data-selected')) !== null
      && (await chip.innerText()).trim().startsWith('Разобрать');
    if (!(await chosen())) throw new Error(`чип очередей не выбран «Разобрать» после перехода с Главной: «${await chip.innerText()}»`);
    const onChip = await queueChipCount(page);
    if (onChip !== number) throw new Error(`на Главной «${number}», у чипа — «${onChip}»`);
    // То же число — у пункта меню: одно правило числа на чип, пункт и карточку.
    const inMenu = await queueOptionCount(page, 'Разобрать');
    if (inMenu !== number) throw new Error(`на Главной «${number}», у пункта меню — «${inMenu}»`);
    // Обновление страницы отбор не снимает: он в адресе.
    await page.reload({ waitUntil: 'networkidle' });
    await chip.waitFor({ timeout: 10_000 });
    if (!(await chosen())) throw new Error('после обновления страницы отбор «Разобрать» снят');
    await pickQueue(page, 'Все счета');
    // Адрес пишет роутер следом за закрытием списка — ждём его, а не читаем один раз (ревью PR #1934).
    await page.waitForURL(u => u.searchParams.get('queue') === null, { timeout: 10_000 })
      .catch(() => { throw new Error('«Все счета» не убрали очередь из адреса'); });
  });
}
