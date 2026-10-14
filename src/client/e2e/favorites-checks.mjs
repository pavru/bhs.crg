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

// Стройка, которую проверки заводят сами, если на стенде их меньше двух: на стенде CI после посева
// стройка одна, а скрыть фильтром можно только вторую (CI PR #1331). Удаляется в finally — наборы
// идут по очереди (`run-suites.mjs`), и следующий её уже не увидит.
const SPARE = 'Избранное: вторая стройка (проверка)';

const stars = page => page.getByTestId('favorite-star');

export async function favoritesChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройки избранного не сброшены: ${reset.status}`);
  const spare = [];
  try {
    let all = (await callApi(page, 'GET', '/constructions')).body;
    while (all.length + spare.length < 2) {
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

    await check('favorites-direct-link-opens-not-favorite', async () => {
      const favorite = (await callApi(page, 'GET', '/account/settings')).body.favoriteConstructions;
      const other = all.find(c => c.id !== favorite);
      await page.goto(`${BASE}/document-sets/${other.id}`);
      await settled(page);
      // Объект открыт полностью, строка говорит, почему его нет в списке; шапка — «не действуют».
      await page.getByTestId('not-favorite-note').waitFor({ timeout: 5000 });
      await page.getByTestId('site-filter-idle').waitFor({ timeout: 5000 });
    });
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
    for (const id of spare) await callApi(page, 'DELETE', `/constructions/${id}`);
  }
}
