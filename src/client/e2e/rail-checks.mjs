// Рейл разделов и подпанель раздела (issue #1429; до неё — свёртка панели в иконки, #1355) —
// проверки живым браузером.
//
// Отдельным файлом, а не своим набором — по той же причине, что пространства и избранное: входы
// прогонов CI упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke`.
//
// ⚠️ Выбор хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленная скрытой
// подпанель прятала бы страницы разделов, и чужие локаторы по тексту меню упали бы. Поэтому
// настройка сбрасывается и до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi, until, railSection, accountAction } from './harness.mjs';

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

    // Подпись раздела счетов — «Затраты» (plan-02, макет П0). Запрет старой подписи держится на
    // положительном якоре: без «Затрат» в рейле его отсутствие «Учёта» ничего бы не значило.
    // Раздел «Планы» здесь не проверяется: пунктов модуля ещё нет, и проверка не могла бы упасть.
    // Отбор раздела по модулю стережёт `railSections.test.ts`; живую проверку заводит plan-09
    // вместе с первым пунктом раздела.
    await check('rail-costs-section-labelled-zatraty', async () => {
      const sections = (await page.locator('#app-rail-nav a').allInnerTexts()).map(t => t.trim());
      if (!sections.includes('Затраты')) throw new Error(`в рейле нет раздела «Затраты»: [${sections}]`);
      if (sections.includes('Учёт')) throw new Error(`раздел счетов подписан по-старому: [${sections}]`);
    });

    await narrowChecks(page, check);
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
  }
}

const sideScroll = page => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

/** Цели касания телефона меньше 44 px — нижняя навигация, шапка, заголовок-меню (issue #1434). */
const smallTargets = page => page.evaluate(() =>
  [...document.querySelectorAll('#app-bottom-nav a, main > header button, [data-testid=section-menu-trigger]')]
    .map(el => ({ el, box: el.getBoundingClientRect() }))
    .filter(({ box }) => box.width > 0 && (box.width < 44 || box.height < 44))
    .map(({ el, box }) => `«${(el.getAttribute('aria-label') ?? el.textContent ?? '').slice(0, 30)}» ${Math.round(box.width)}×${Math.round(box.height)}`));

/**
 * Узкий экран (issue #1355, #1434): 390 и 375 px — ширины обычных телефонов. Размер окна
 * возвращается в finally: следующие проверки набора идут на рабочем столе, и оставленный телефон
 * уронил бы их на чужом.
 */
async function narrowChecks(page, check) {
  const desktop = page.viewportSize();
  // Рабочие разделы рейла — всё, кроме «Системы»: она на телефоне в меню профиля.
  const work = (await page.locator('#app-rail-nav a').allInnerTexts()).map(t => t.trim()).filter(t => t !== 'Система');
  if (!work.length) throw new Error('в рейле нет ни одного рабочего раздела');
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('narrow-screen-bottom-nav-reaches-every-section', async () => {
      if (await page.getByTestId('rail').isVisible()) throw new Error('на 390 px панель осталась у края');
      // Шапка — одной строкой и на телефоне (#1432): пустая вторая строка под ней была бы лишней.
      const header = await page.locator('main > header').boundingBox();
      if (!header || header.height > 49) throw new Error(`шапка на 390 px высотой ${header?.height}px — не в одну строку`);
      // Гамбургера нет: разделы — внизу (#1434).
      if (await page.getByRole('button', { name: 'Меню', exact: true }).count()) throw new Error('в шапке остался гамбургер');
      // Те же рабочие разделы, что в рейле рабочего стола: копия, разошедшаяся с рейлом, прятала бы разделы.
      const shown = (await page.getByTestId('bottom-nav').getByRole('link').allInnerTexts()).map(t => t.trim());
      if (shown.join('|') !== work.join('|')) throw new Error(`внизу [${shown}], в рейле рабочего стола [${work}]`);
      for (const width of [390, 375]) {
        await page.setViewportSize({ width, height: 844 });
        const extra = await sideScroll(page);
        if (extra > 0) throw new Error(`на ${width} px каркас шире экрана на ${extra}px — страница едет вбок`);
        const small = await smallTargets(page);
        if (small.length) throw new Error(`на ${width} px цели касания меньше 44 px: ${small.join(', ')}`);
      }
      await page.setViewportSize({ width: 390, height: 844 });
    });

    // Основное действие экрана на телефоне — плавающая кнопка с подписью (#1434): та же кнопка экрана
    // через `?do=`, а её двойник в шапке спрятан.
    await check('narrow-primary-action-is-a-labelled-fab', async () => {
      const fab = page.getByTestId('primary-fab');
      const label = (await fab.innerText()).trim();
      if (label !== 'Новая стройка') throw new Error(`плавающая кнопка строек — «${label}»`);
      if (await page.locator('main header').getByRole('button', { name: 'Новая стройка', exact: true }).isVisible())
        throw new Error('кнопка «Новая стройка» в шапке осталась рядом с плавающей');
      await fab.click();
      const dialog = page.getByRole('dialog', { name: 'Новая стройка' });
      await dialog.waitFor();
      if (new URL(page.url()).searchParams.has('do')) throw new Error(`действие осталось в адресе: ${page.url()}`);
      await page.keyboard.press('Escape');
      await dialog.waitFor({ state: 'detached' });
    });

    // Подпанели на телефоне нет — страницы раздела раскрывает заголовок; «Систему» — меню профиля.
    await check('narrow-title-opens-the-pages-of-the-section', async () => {
      await accountAction(page, 'Администрирование');
      await page.waitForURL(url => !url.pathname.startsWith('/document-sets'));
      await settled(page);
      await page.getByTestId('section-menu-trigger').click();
      const menu = page.getByTestId('section-menu');
      await menu.waitFor();
      // Выбор страницы закрывает меню — иначе оно лежало бы поверх открытой страницы.
      await menu.getByRole('menuitem', { name: 'Роли и права' }).click();
      await page.waitForURL(/\/roles/);
      await menu.waitFor({ state: 'detached' });
      const caption = (await page.getByTestId('section-caption').innerText()).trim();
      if (caption !== 'Администрирование') throw new Error(`над заголовком «${caption}», ждали имя раздела «Администрирование»`);
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

    // Системная «Назад» на телефоне закрывает верхний слой, а не страницу под ним (#1418): меню,
    // окно, окно с вводом — тем же вопросом, что крестик. Адрес при этом не меняется.
    await check('narrow-back-closes-the-top-layer', async () => {
      const here = page.url();
      const stayed = (what) => { if (page.url() !== here) throw new Error(`«Назад» с ${what} увела на ${page.url()}`); };

      await page.getByTestId('section-menu-trigger').click();
      const menu = page.getByTestId('section-menu');
      await menu.waitFor();
      await page.goBack();
      await menu.waitFor({ state: 'detached' });
      stayed('меню раздела');

      await page.getByTestId('account-menu').click();
      const account = page.getByRole('menu');
      await account.waitFor();
      await page.goBack();
      await account.waitFor({ state: 'detached' });
      stayed('меню профиля');

      const dialog = page.getByRole('dialog', { name: 'Новая роль' });
      await page.getByRole('button', { name: 'Новая роль' }).click();
      await dialog.waitFor();
      await page.goBack();
      await dialog.waitFor({ state: 'detached' });
      stayed('пустого окна');

      // С вводом — вопрос; «Назад» при вопросе — «Продолжить редактирование», как Esc.
      await page.getByRole('button', { name: 'Новая роль' }).click();
      await dialog.waitFor();
      await dialog.getByRole('textbox').first().fill('Набранное');
      await page.goBack();
      const ask = dialog.getByText('Закрыть без сохранения?');
      await ask.waitFor();
      stayed('окна с вводом');
      await page.goBack();
      await ask.waitFor({ state: 'detached' });
      if (!(await dialog.isVisible())) throw new Error('«Назад» при вопросе закрыла окно с набранным');
      stayed('вопроса');
      await page.goBack();
      await ask.waitFor();
      // «Закрыть» вопроса, а не крестик окна с тем же именем.
      await dialog.locator('div', { has: page.getByText('Закрыть без сохранения?') }).last()
        .getByRole('button', { name: 'Закрыть', exact: true }).click();
      await dialog.waitFor({ state: 'detached' });
      stayed('закрытого окна');

      // Слоёв больше нет — «Назад» уводит со страницы, одним нажатием: записи закрытых слоёв не
      // съедают нажатий.
      // Сверяется по записи роутера (`idx`): соседняя запись бывает с тем же адресом.
      const idx = await page.evaluate(() => history.state?.idx);
      const at = (want) => page.waitForFunction(i => history.state?.idx === i && !history.state?.layer, want, { timeout: 5000 });
      await page.goBack();
      await at(idx - 1);
      await page.goForward();
      await at(idx);
      stayed('«Вперёд» обратно');
      await settled(page);
    });

    // Меню раздела, открытое на телефоне, закрывается, когда окно расширили: на рабочем столе его
    // место заняла подпанель, и меню лежало бы поверх неё.
    await check('narrow-section-menu-closes-when-window-widens', async () => {
      await page.getByTestId('section-menu-trigger').click();
      const menu = page.getByTestId('section-menu');
      await menu.waitFor();
      await page.setViewportSize(desktop ?? { width: 1280, height: 720 });
      await menu.waitFor({ state: 'detached' });
      if (!(await page.getByTestId('rail').isVisible())) throw new Error('на рабочем столе панель не вернулась к краю');
      if (await page.getByTestId('bottom-nav').isVisible()) throw new Error('на рабочем столе осталась нижняя навигация');
    });
  } finally {
    if (desktop) await page.setViewportSize(desktop);
  }
}
