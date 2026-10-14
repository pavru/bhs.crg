// Избранные стройки (issue #1217, #1218) — проверки живым браузером.
//
// Отдельным файлом, а не своим набором: входы прогонов CI упёрлись в предел частоты (считает
// `suites.test.mjs`), и новый набор со своим входом туда не помещается. Проверки едут внутри
// `settings-smoke` под его входом администратора.
//
// ⚠️ Избранное хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленное
// избранное урезало бы им список строек, и они упали бы на чужом. Поэтому настройки сбрасываются и
// до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi } from './harness.mjs';

const RESET = { favoriteConstructions: null, favoritesFilter: null };

// Стройки, которые проверки заводят сами, если на стенде их меньше трёх: на стенде CI после посева
// стройка одна (CI PR #1331), а проверке переключателя после звезды нужна третья — неизбранная
// рядом с двумя избранными. Удаляются в finally — наборы идут по очереди (`run-suites.mjs`), и
// следующий их уже не увидит.
const SPARE = 'Избранное: вторая стройка (проверка)';

const stars = page => page.getByTestId('favorite-star');

export async function favoritesChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройки избранного не сброшены: ${reset.status}`);
  const spare = [];
  try {
    let all = (await callApi(page, 'GET', '/constructions')).body;
    while (all.length + spare.length < 3) {
      const created = await callApi(page, 'POST', '/constructions', { name: `${SPARE} ${spare.length + 1}` });
      if (created.status !== 201 && created.status !== 200) throw new Error(`вторая стройка не заведена: ${created.status}`);
      spare.push(created.body.id);
    }
    if (spare.length) all = (await callApi(page, 'GET', '/constructions')).body;
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('favorites-empty-header-is-quiet-button', async () => {
      // Избранного нет — сегмента нет, на его месте тихая кнопка к выбору.
      await page.getByTestId('site-filter-empty').waitFor({ timeout: 5000 });
      if (await page.getByTestId('site-filter-switch').count()) throw new Error('сегмент виден без избранного');
    });

    await check('favorites-first-star-keeps-the-screen', async () => {
      await stars(page).first().click();
      await settled(page);
      // Первая звезда не сжимает список под курсором — до ухода с экрана (§62.2).
      const left = await stars(page).count();
      if (left !== all.length) throw new Error(`после первой звезды карточек ${left}, а строек ${all.length}`);
      const segment = await page.getByTestId('site-filter-switch').innerText();
      if (!/Избранные\s*1/.test(segment) || !new RegExp(`Все\\s*${all.length}`).test(segment))
        throw new Error(`сегмент: «${segment}»`);
    });

    await check('favorites-filter-list-after-reload', async () => {
      await page.reload();
      await settled(page);
      const left = await stars(page).count();
      if (left !== 1) throw new Error(`после перезагрузки карточек ${left}, ждали одну`);
      const note = await page.getByTestId('site-filter-note').innerText();
      if (!note.includes(`скрыто: ${all.length - 1}`)) throw new Error(`строка у списка: «${note}»`);
    });

    await check('favorites-switch-shows-all-and-is-stored', async () => {
      await page.getByTestId('site-filter-switch').getByRole('button', { name: /^Все/ }).click();
      await settled(page);
      if ((await stars(page).count()) !== all.length) throw new Error('«Все» не показало все стройки');
      // Хранит сервер: с другого компьютера человек увидит то же.
      const stored = (await callApi(page, 'GET', '/account/settings')).body.favoritesFilter;
      if (stored !== 'off') throw new Error(`выключенный фильтр не сохранён: ${stored}`);
      await page.getByTestId('site-filter-switch').getByRole('button', { name: /Избранные/ }).click();
      await settled(page);
      if ((await stars(page).count()) !== 1) throw new Error('«Избранные» не вернули отбор');
    });

    await check('favorites-switch-after-star-filters-the-list', async () => {
      // Звезда держит карточки на экране до ухода с него, но явное переключение в шапке это
      // удержание снимает — иначе «Избранные» горели бы над неурезанным списком (ревью PR #1331).
      await page.getByTestId('site-filter-switch').getByRole('button', { name: /^Все/ }).click();
      await settled(page);
      await page.locator('[data-testid="favorite-star"][aria-pressed="false"]').first().click();
      await settled(page);
      await page.getByTestId('site-filter-switch').getByRole('button', { name: /Избранные/ }).click();
      await settled(page);
      const left = await stars(page).count();
      if (left !== 2) throw new Error(`после звезды и «Избранные» карточек ${left}, ждали две`);
    });

    await check('favorites-new-construction-joins-favorites', async () => {
      // Избранное непусто — созданная стройка попадает в него и открывается без строки «не в
      // избранном»: справочник перечитан до перехода на её страницу (ревью PR #1331).
      await page.getByRole('button', { name: 'Новая стройка' }).first().click();
      await page.getByLabel('Название стройки').fill(`${SPARE} новая`);
      await page.getByRole('button', { name: 'Создать' }).click();
      await page.waitForURL(/\/document-sets\/[0-9a-f-]{36}$/);
      spare.push(page.url().split('/').pop());
      await settled(page);
      if (await page.getByTestId('not-favorite-note').count()) throw new Error('новая стройка названа «не в избранном»');
      const stored = (await callApi(page, 'GET', '/account/settings')).body.favoriteConstructions.split(',');
      if (!stored.includes(spare.at(-1))) throw new Error('новая стройка не попала в избранное');
    });

    await check('favorites-write-keeps-construction-this-page-has-not-seen', async () => {
      // «С другого компьютера» заводят стройку и отмечают её — адресом, мимо кэша этой страницы.
      // Звезда здесь не должна снять её как «удалённую»: справочник этой страницы её ещё не видел, а
      // сервер — видит (ревью PR #1331).
      const created = await callApi(page, 'POST', '/constructions', { name: `${SPARE} с другого компьютера` });
      const elsewhere = created.body.id;
      spare.push(elsewhere);
      const before = (await callApi(page, 'GET', '/account/settings')).body.favoriteConstructions;
      await callApi(page, 'PUT', '/account/settings', { favoriteConstructions: `${before},${elsewhere}` });
      // В список — переходом внутри приложения: перезагрузка перечитала бы справочник.
      await page.getByRole('navigation').getByRole('link', { name: 'Стройки' }).first().click();
      await settled(page);
      // Под фильтром в списке только избранные — снимаем одну из них: запись та же.
      await page.locator('[data-testid="favorite-star"][aria-pressed="true"]').first().click();
      await settled(page);
      const stored = (await callApi(page, 'GET', '/account/settings')).body.favoriteConstructions.split(',');
      if (!stored.includes(elsewhere)) throw new Error('звезда сняла стройку, отмеченную на другом компьютере');
    });

    await check('favorites-direct-link-opens-not-favorite', async () => {
      const favorite = (await callApi(page, 'GET', '/account/settings')).body.favoriteConstructions.split(',');
      const other = all.find(c => !favorite.includes(c.id));
      await page.goto(`${BASE}/document-sets/${other.id}`);
      await settled(page);
      // Объект открыт полностью, строка говорит, почему его нет в списке; шапка — «не действуют».
      await page.getByTestId('not-favorite-note').waitFor({ timeout: 5000 });
      await page.getByTestId('site-filter-idle').waitFor({ timeout: 5000 });
    });

    await check('favorites-periods-table-filtered-closing-names-all', async () => {
      // Учётный период (#1220): урезана таблица, а закрытие компании называет все стройки. Сам период
      // не закрываем — диалог только читается: закрытие на стенде легло бы под следующие наборы.
      await callApi(page, 'PUT', '/account/settings', { favoriteConstructions: all[0].id, favoritesFilter: null });
      const total = (await callApi(page, 'GET', '/periods')).body.constructions.length;
      await page.goto(`${BASE}/periods`);
      await settled(page);
      const table = page.locator('table').first();
      const rows = await table.locator('tbody tr').count();
      if (rows !== 2) throw new Error(`в таблице периода строк ${rows}, ждали компанию и одну стройку`);
      const count = await page.getByTestId('period-sites-count').innerText();
      if (!count.startsWith(`1 из ${total} `)) throw new Error(`шапка таблицы: «${count}»`);
      const note = await page.getByTestId('site-filter-note').innerText();
      if (!note.includes(`Ещё ${total - 1} `)) throw new Error(`строка под таблицей: «${note}»`);
      await table.locator('tbody tr').first().getByRole('button', { name: 'Закрыть период' }).click();
      const dialog = page.getByRole('dialog');
      const text = await dialog.innerText();
      if (!text.includes(`все стройки — ${total}, в том числе ${total - 1}`))
        throw new Error(`диалог закрытия компании не назвал скрытые: «${text.slice(0, 300)}»`);
      await page.keyboard.press('Escape');
      await dialog.waitFor({ state: 'detached', timeout: 5000 });
    });

    await check('favorites-settings-failure-shows-everything', async () => {
      // Настройки не пришли — фильтр не действует и говорит об этом, а список строек полный. Раньше
      // «загрузка» ждала настроек без конца и держала сам список (ревью PR #1331).
      await page.route('**/api/account/settings', route =>
        route.request().method() === 'GET' ? route.fulfill({ status: 500, body: '{}' }) : route.continue());
      try {
        await page.goto(`${BASE}/document-sets`);
        await page.getByTestId('site-filter-error').waitFor({ timeout: 20000 });
        const left = await stars(page).count();
        if (left !== 0 && left !== all.length) throw new Error(`при сбое настроек карточек ${left} из ${all.length}`);
        const cards = await page.locator('h3').count();
        if (cards < all.length) throw new Error(`при сбое настроек видно ${cards} строек из ${all.length}`);
      } finally {
        await page.unroute('**/api/account/settings');
      }
    });
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
    for (const id of spare) await callApi(page, 'DELETE', `/constructions/${id}`);
  }
}
