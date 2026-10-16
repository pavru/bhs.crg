// Свёрнутая боковая панель (issue #1355) — проверки живым браузером.
//
// Отдельным файлом, а не своим набором — по той же причине, что пространства и избранное: входы
// прогонов CI упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke`.
//
// ⚠️ Выбор хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленная свёрнутой
// панель прятала бы подписи пунктов, и чужие локаторы по тексту меню упали бы. Поэтому настройка
// сбрасывается и до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi, until } from './harness.mjs';

const RESET = { rail: null };

const railWidth = page => page.getByTestId('rail').evaluate(el => el.getBoundingClientRect().width);

export async function railChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройка панели не сброшена: ${reset.status}`);
  try {
    // Зеркало в браузере осталось бы от прошлых прогонов: первый кадр взял бы его, а не умолчание.
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('rail-collapses-to-icons-with-names-kept', async () => {
      const wide = await railWidth(page);
      if (wide < 200) throw new Error(`панель по умолчанию не развёрнута: ${wide}px`);

      await page.getByRole('button', { name: 'Свернуть панель' }).click();
      await until(async () => {
        const w = await railWidth(page);
        if (w >= 80) throw new Error(`панель не свернулась: ${w}px`);
      });

      // Пункт без подписи на экране обязан остаться названным: подсказкой для глаза и именем для
      // диктора. Пропади имя — меню превратилось бы в ряд неотличимых кнопок.
      const link = page.locator('#app-rail-nav a').first();
      const title = await link.getAttribute('title');
      if (!title) throw new Error('у пункта свёрнутой панели нет подсказки');
      const spoken = await link.locator('.sr-only').allTextContents();
      if (!spoken.includes(title)) throw new Error(`подпись пункта «${title}» не оставлена диктору: ${spoken}`);
    });

    await check('rail-collapsed-survives-reload-and-lives-on-server', async () => {
      await until(async () => {
        const { body } = await callApi(page, 'GET', '/account/settings');
        if (body?.rail !== 'collapsed') throw new Error(`свёрнутая панель не записана на сервер: ${body?.rail}`);
      });

      await page.reload();
      await settled(page);
      const width = await railWidth(page);
      if (width >= 80) throw new Error(`после перезагрузки панель развёрнута: ${width}px`);

      await page.getByRole('button', { name: 'Развернуть панель' }).click();
      await until(async () => {
        const w = await railWidth(page);
        if (w <= 200) throw new Error(`панель не развернулась обратно: ${w}px`);
      });
    });
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
  }
}
