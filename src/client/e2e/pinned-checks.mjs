// Закреплённые страницы (ТЗ AUTH-16.4, AUTH-16.7; work-04, макет Р0 — стол А) — проверки живым браузером.
//
// Отдельным файлом, а не своим набором — по той же причине, что пространства и избранное: входы
// прогонов CI упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke` под его входом
// администратора: у него модуль счетов и право на накладные.
//
// ⚠️ Список хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленное закрепление
// добавило бы в каждую подпанель группу «Закреплённые», и чужие проверки текста меню читали бы лишнее.
// Поэтому настройки сбрасываются и до проверок, и после — в finally, адресом, а не через экран.
//
// Чем ломается: закрепления в браузере вместо сервера (зеркало `crg-pinned` вместо `pinnedSections`) —
// краснеет «чистое хранилище»; фильтр меню, удаляющий недоступное из СПИСКА, — краснеет профиль.

import { BASE, settled, callApi, callApiOk, watchRequests } from './harness.mjs';

const RESET = { pinnedSections: null, pinnedGroup: null };
const WAYBILL_RIGHT = 'costs.waybill.read';

const panel = page => page.getByTestId('section-panel');
const group = page => page.getByTestId('pinned-group');

/** Записи списка на сервере — ответом на ЭТОТ запрос, а не тишиной: проверка стережёт, что выбор дошёл. */
async function saved(page, what, act) {
  const [answer] = await Promise.all([
    page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith('/account/settings')
      && (r.request().postData() ?? '').includes('pinnedSections'), { timeout: 10_000 })
      .catch(e => { throw new Error(`${what}: запись на сервер не ушла (${e.message.split('\n')[0]})`); }),
    act(),
  ]);
  if (!answer.ok()) throw new Error(`${what}: запись отклонена сервером — ${answer.status()}`);
  return JSON.parse((await answer.json())?.pinnedSections ?? '[]');
}

/**
 * Новое окно с ТОЛЬКО токенами входа: без зеркала закреплений и прочей памяти браузера — «другая
 * машина». Токен может лежать и в sessionStorage (снят «Запомнить меня»), а `storageState` его не берёт —
 * переносим руками.
 */
async function cleanContext(browser, page) {
  const tokens = await page.evaluate(() => Object.fromEntries(['access_token', 'refresh_token']
    .map(key => [key, localStorage.getItem(key) ?? sessionStorage.getItem(key)]).filter(([, v]) => v)));
  if (!tokens.access_token) throw new Error('токен входа не найден — переносить в чистое окно нечего');
  const context = await browser.newContext({ viewport: page.viewportSize() });
  await context.addInitScript(saved => {
    if (sessionStorage.getItem('pinned-checks-seeded')) return;
    sessionStorage.setItem('pinned-checks-seeded', '1');
    localStorage.clear();
    for (const [key, value] of Object.entries(saved)) localStorage.setItem(key, value);
  }, tokens);
  return context;
}

export async function pinnedChecks(page, browser, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройки закреплений не сброшены: ${reset.status}`);
  const access = (await callApiOk(page, 'GET', '/account/access'));
  if (!access.permissions.includes(WAYBILL_RIGHT))
    throw new Error(`у входа набора нет ${WAYBILL_RIGHT} — закреплять в проверке нечего`);
  try {
    await page.goto(`${BASE}/invoices`);
    await settled(page);

    await check('pin-from-row-menu-lands-first-in-the-panel', async () => {
      if (await group(page).count()) throw new Error('группа «Закреплённые» есть до закрепления');
      const row = panel(page).getByRole('link', { name: 'Накладные', exact: true });
      await row.hover();
      await panel(page).getByRole('button', { name: 'Действия: Накладные' }).click();
      const stored = await saved(page, 'закрепление', () =>
        page.getByRole('menuitem', { name: /Закрепить страницу/ }).click());
      if (JSON.stringify(stored) !== '["/waybills"]') throw new Error(`на сервере ${JSON.stringify(stored)}`);
      const pinned = group(page).getByRole('link');
      await pinned.first().waitFor({ timeout: 5000 });
      const text = await pinned.first().innerText();
      if (!text.includes('Накладные') || !text.includes('Учёт затрат'))
        throw new Error(`пункт группы — «${text}», ждали «Накладные» с разделом «Учёт затрат»`);
      // Первой в подпанели: раньше любой страницы раздела.
      const first = await panel(page).locator('[data-testid="pinned-group"], a').first().getAttribute('data-testid');
      if (first !== 'pinned-group') throw new Error('группа «Закреплённые» стоит не первой в подпанели');
    });

    await check('pinned-page-comes-from-the-server-into-a-clean-browser', async () => {
      // Другой раздел — «Стройки»: группа одна на все рабочие подпанели.
      const context = await cleanContext(browser, page);
      try {
        const fresh = await context.newPage();
        // Вход здесь не нужен (токены перенесены), а наблюдение за запросами ставит именно он — до перехода.
        await watchRequests(fresh);
        await fresh.goto(`${BASE}/document-sets`);
        await settled(fresh);
        const link = group(fresh).getByRole('link', { name: /Накладные/ });
        await link.waitFor({ timeout: 5000 }).catch(() => {
          throw new Error('в чистом браузере группы «Закреплённые» с «Накладными» нет — список не пришёл с сервера');
        });

        // Право сняли (ответ доступа правится в ЭТОМ окне — перехват на общей странице не включается):
        // в группе страницы нет, в профиле она строкой с причиной, и из списка не ушла.
        await context.route('**/api/account/access', async route => {
          const response = await route.fetch();
          const body = await response.json();
          body.permissions = body.permissions.filter(p => p !== WAYBILL_RIGHT);
          await route.fulfill({ response, json: body });
        });
        await fresh.goto(`${BASE}/invoices`);
        await settled(fresh);
        if (await group(fresh).count()) throw new Error('без права страница осталась в группе «Закреплённые»');
        await fresh.goto(`${BASE}/profile`);
        await settled(fresh);
        const row = fresh.getByTestId('pinned-row').filter({ hasText: 'Накладные' });
        await row.waitFor({ timeout: 5000 }).catch(() => {
          throw new Error('без права «Накладные» пропали и из профиля');
        });
        if ((await row.getAttribute('data-kind')) !== 'permission')
          throw new Error(`строка профиля — «${await row.getAttribute('data-kind')}», ждали «нет права»`);
        const note = await row.innerText();
        if (!note.includes(`Нет доступа: нужно право ${WAYBILL_RIGHT}`)) throw new Error(`пометка строки: «${note}»`);
        const stored = JSON.parse((await callApiOk(fresh, 'GET', '/account/settings')).pinnedSections ?? '[]');
        if (!stored.includes('/waybills')) throw new Error('недоступная страница ушла из списка на сервере');
      } finally {
        await context.close();
      }
    });

    await check('unpin-from-profile-removes-the-group', async () => {
      await page.goto(`${BASE}/profile`);
      await settled(page);
      const row = page.getByTestId('pinned-row').filter({ hasText: 'Накладные' });
      const stored = await saved(page, 'открепление', () => row.getByRole('button', { name: 'Открепить' }).click());
      if (stored.length) throw new Error(`после «Открепить» на сервере ${JSON.stringify(stored)}`);
      await page.goto(`${BASE}/invoices`);
      await settled(page);
      if (await group(page).count()) throw new Error('после открепления группа «Закреплённые» осталась');
    });
  } finally {
    await callApiOk(page, 'PUT', '/account/settings', RESET);
  }
}
