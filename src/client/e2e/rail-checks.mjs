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
      const spoken = (await link.locator('.sr-only').allTextContents()).join('').trim();
      if (!spoken) throw new Error('подпись пункта свёрнутой панели не оставлена диктору');
      await link.hover();
      // Подсказка Radix — скрытая копия с role=tooltip: видимой её не дождаться, только в разметке.
      await page.locator('[role=tooltip]', { hasText: spoken }).first().waitFor({ state: 'attached', timeout: 3000 })
        .catch(() => { throw new Error(`у пункта «${spoken}» свёрнутой панели нет подсказки`); });
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

    await narrowChecks(page, check);
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
  }
}

const sideScroll = page => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

/**
 * Узкий экран (issue #1355): 390 px — ширина обычного телефона. Размер окна возвращается в finally:
 * следующие проверки набора идут на рабочем столе, и оставленный телефон уронил бы их на чужом.
 */
async function narrowChecks(page, check) {
  const desktop = page.viewportSize();
  const sections = await page.locator('#app-rail-nav a').count();
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('narrow-screen-menu-reaches-every-section', async () => {
      if (await page.getByTestId('rail').isVisible()) throw new Error('на 390 px панель осталась у края');
      const extra = await sideScroll(page);
      if (extra > 0) throw new Error(`каркас шире экрана на ${extra}px — страница едет вбок`);

      await page.getByRole('button', { name: 'Меню' }).click();
      const drawer = page.getByTestId('rail-drawer');
      await drawer.waitFor();
      // Тот же список, что у панели на рабочем столе: копия, разошедшаяся с ней, прятала бы разделы.
      const shown = await drawer.locator('#app-drawer-nav a').count();
      if (shown !== sections) throw new Error(`в меню ${shown} разделов, на рабочем столе ${sections}`);

      // Выбор раздела закрывает меню — иначе оно лежало бы поверх открытого раздела.
      await drawer.getByRole('link', { name: 'Роли и права' }).click();
      await page.waitForURL(/\/roles/);
      await drawer.waitFor({ state: 'detached' });
    });

    await check('narrow-list-and-card-take-turns', async () => {
      await settled(page);
      const nav = page.getByTestId('ld-nav');
      const card = page.getByTestId('ld-detail');
      if (!(await nav.isVisible()) || await card.isVisible()) {
        throw new Error('сплит на узком экране начал не со списка');
      }
      await nav.getByRole('button').last().click();
      await card.waitFor({ state: 'visible' });
      if (await nav.isVisible()) throw new Error('после выбора роли список остался рядом с карточкой');

      await card.getByRole('button', { name: 'К списку' }).click();
      await nav.waitFor({ state: 'visible' });
      if (await card.isVisible()) throw new Error('«К списку» не убрал карточку');
      const extra = await sideScroll(page);
      if (extra > 0) throw new Error(`страница ролей шире экрана на ${extra}px`);
    });

    // Окно обычного размера на телефоне — во весь экран: плавающая карточка резала бы поля.
    await check('narrow-dialog-fills-the-screen', async () => {
      await page.getByRole('button', { name: 'Новая роль' }).click();
      const dialog = page.getByRole('dialog', { name: 'Новая роль' });
      await dialog.waitFor();
      const box = await dialog.boundingBox();
      if (!box || box.x > 0 || box.y > 0 || box.width < 389 || box.height < 843) {
        throw new Error(`окно не во весь экран: ${JSON.stringify(box)}`);
      }
      await page.keyboard.press('Escape');
      await dialog.waitFor({ state: 'detached' });
    });

    // Меню, открытое на телефоне, закрывается, когда окно расширили: иначе оно лежало бы поверх
    // панели, вернувшейся к краю, и держало бы страницу под затемнением.
    await check('narrow-menu-closes-when-window-widens', async () => {
      await page.getByRole('button', { name: 'Меню' }).click();
      const drawer = page.getByTestId('rail-drawer');
      await drawer.waitFor();
      await page.setViewportSize(desktop ?? { width: 1280, height: 720 });
      await drawer.waitFor({ state: 'detached' });
      if (!(await page.getByTestId('rail').isVisible())) throw new Error('на рабочем столе панель не вернулась к краю');
    });
  } finally {
    if (desktop) await page.setViewportSize(desktop);
  }
}
