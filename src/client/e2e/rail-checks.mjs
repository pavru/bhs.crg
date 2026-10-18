// Рейл разделов и подпанель раздела (issue #1429; до неё — свёртка панели в иконки, #1355) —
// проверки живым браузером.
//
// Отдельным файлом, а не своим набором — по той же причине, что пространства и избранное: входы
// прогонов CI упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke`.
//
// ⚠️ Выбор хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленная скрытой
// подпанель прятала бы страницы разделов, и чужие локаторы по тексту меню упали бы. Поэтому
// настройка сбрасывается и до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi, until, railSection } from './harness.mjs';

const RESET = { rail: null };

const panel = page => page.getByTestId('section-panel');

export async function railChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройка подпанели не сброшена: ${reset.status}`);
  try {
    // Зеркало в браузере осталось бы от прошлых прогонов: первый кадр взял бы его, а не умолчание.
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
    await page.goto(`${BASE}/roles`);
    await settled(page);

    await check('section-panel-shows-the-pages-of-the-section', async () => {
      // Окно прогона шире 1200 px, а человек ничего не выбирал — подпанель на месте.
      if (!(await panel(page).isVisible()))
        throw new Error(`подпанель не показана по умолчанию на ${page.viewportSize()?.width}px`);
      const title = (await panel(page).getByRole('heading', { level: 2 }).innerText()).trim();
      if (title !== 'Администрирование') throw new Error(`у «Системы» подпанель «${title}»`);
      for (const group of ['Документы', 'Люди и доступ', 'Наблюдение', 'Общее'])
        if (!(await panel(page).getByRole('heading', { level: 3, name: group }).count()))
          throw new Error(`в «Администрировании» нет группы «${group}»`);
      const width = await page.getByTestId('rail').evaluate(el => el.getBoundingClientRect().width);
      // Рейл 88 + подпанель 240 + черта.
      if (Math.abs(width - 329) > 2) throw new Error(`рейл с подпанелью шириной ${width}px, ждали 329`);
    });

    await check('section-panel-hides-and-lives-on-server', async () => {
      await panel(page).getByRole('button', { name: 'Скрыть панель раздела' }).click();
      await panel(page).waitFor({ state: 'detached' });
      await until(async () => {
        const { body } = await callApi(page, 'GET', '/account/settings');
        if (body?.rail !== 'collapsed') throw new Error(`скрытая подпанель не записана на сервер: ${body?.rail}`);
      });

      await page.reload();
      await settled(page);
      if (await panel(page).count()) throw new Error('после перезагрузки подпанель вернулась');

      // Свой раздел в рейле при скрытой подпанели — «покажи страницы», а не переход на первую.
      await railSection(page, 'Система').click();
      await panel(page).waitFor();
      if (new URL(page.url()).pathname !== '/roles')
        throw new Error(`щелчок по своему разделу увёл на ${new URL(page.url()).pathname}`);

      // И кнопка у края страницы — второй вход к скрытой подпанели.
      await panel(page).getByRole('button', { name: 'Скрыть панель раздела' }).click();
      await panel(page).waitFor({ state: 'detached' });
      await page.getByRole('button', { name: 'Показать панель раздела' }).click();
      await panel(page).waitFor();
    });

    // В карточке стройки подпанели нет: её место у навигации стройки, две панели рядом не стоят.
    await check('site-card-has-no-section-panel', async () => {
      const sites = (await callApi(page, 'GET', '/constructions')).body;
      if (!Array.isArray(sites) || !sites.length) throw new Error('нет ни одной стройки — нужны демо-данные');
      await page.goto(`${BASE}/document-sets`);
      await settled(page);
      if (!(await panel(page).isVisible())) throw new Error('у списка строек нет подпанели');
      await page.goto(`${BASE}/document-sets/${sites[0].id}`);
      await settled(page);
      if (await panel(page).count()) throw new Error('в карточке стройки стоит подпанель раздела');
      const current = await page.locator('#app-rail-nav a[aria-current="true"]').innerText();
      if (current.trim() !== 'Стройки') throw new Error(`в карточке стройки текущим помечен раздел «${current.trim()}»`);
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
  if (!sections) throw new Error('в рейле нет ни одного раздела');
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('narrow-screen-menu-reaches-every-section', async () => {
      if (await page.getByTestId('rail').isVisible()) throw new Error('на 390 px панель осталась у края');
      // Шапка — одной строкой и на телефоне (#1432): пустая вторая строка под ней была бы лишней.
      const header = await page.locator('main > header').boundingBox();
      if (!header || header.height > 49) throw new Error(`шапка на 390 px высотой ${header?.height}px — не в одну строку`);
      const extra = await sideScroll(page);
      if (extra > 0) throw new Error(`каркас шире экрана на ${extra}px — страница едет вбок`);

      await page.getByRole('button', { name: 'Меню' }).click();
      const drawer = page.getByTestId('rail-drawer');
      await drawer.waitFor();
      // Те же разделы, что в рейле рабочего стола, — все сразу, каждый со страницами: копия,
      // разошедшаяся с рейлом, прятала бы разделы.
      const shown = await drawer.locator('#app-drawer-nav h2').count();
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
