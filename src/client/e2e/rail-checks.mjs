// Рейл разделов и подпанель раздела (issue #1429; до неё — свёртка панели в иконки, #1355) —
// проверки живым браузером.
//
// Отдельным файлом, а не своим набором — по той же причине, что пространства и избранное: входы
// прогонов CI упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke`.
//
// ⚠️ Выбор хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленная скрытой
// подпанель прятала бы страницы разделов, и чужие локаторы по тексту меню упали бы. Поэтому
// настройка сбрасывается и до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi, callApiOk, railSection, accountAction, emulateTouch } from './harness.mjs';

const RESET = { rail: null };

const panel = page => page.getByTestId('section-panel');

/**
 * Действие, переключающее подпанель, — и ответ на ЕГО запись. Экран показывает выбор по нажатию, а
 * запись уходит следом (`useSyncedPreference`), поэтому появление подпанели в DOM о сервере не говорит
 * ничего. Без ожидания ответа следующий переход страницы обрывал запись или читал прежнее «скрыто» —
 * и следующая проверка падала через раз (прогон 42744201478 на PR #1862). Ждём каждую запись, а не
 * только последнюю: две записи подряд по разным соединениям сервер вправе применить в любом порядке.
 *
 * Именно ответ на ЭТОТ запрос, а не `settled()`: та дождалась бы тишины и тогда, когда записи не было
 * вовсе, — а сценарий стережёт как раз то, что выбор дошёл до сервера. Ответ записи — все настройки
 * учётной записи после неё, поэтому состояние сервера сверяется по нему, без отдельного чтения.
 */
async function toggled(page, rail, what, act) {
  const [answer] = await Promise.all([
    page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith('/account/settings')
      && sentRail(r.request()) === rail, { timeout: 10_000 })
      .catch(e => { throw new Error(`${what}: запись «${rail}» на сервер не ушла (${e.message.split('\n')[0]})`); }),
    act(),
  ]);
  if (!answer.ok()) throw new Error(`${what}: запись «${rail}» отклонена сервером — ${answer.status()}`);
  const stored = (await answer.json())?.rail;
  if (stored !== rail) throw new Error(`${what}: после записи «${rail}» на сервере «${stored}»`);
}

/** Что запрос записывает в `rail`; тело не JSON — значит, не наша запись, а не повод уронить ожидание. */
function sentRail(request) {
  try { return request.postDataJSON()?.rail; } catch { return undefined; }
}

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
      // Самый длинный заголовок раздела — в одну строку: пункты 48 делают подпанель выше экрана, и
      // полоса прокрутки отнимает ширину у заголовка (sharedui-md3-15). Браузер прогонов полос не рисует
      // (Playwright прячет их), поэтому место меряется за вычетом тонкой полосы, если её не видно: иначе
      // проверка не упала бы ровно на том, ради чего заведена.
      const fit = await panel(page).getByRole('heading', { level: 2 }).evaluate(h => {
        const nav = h.closest('nav');
        const bar = nav.offsetWidth - nav.clientWidth;
        const ctx = document.createElement('canvas').getContext('2d');
        ctx.font = getComputedStyle(h).font;
        return { word: ctx.measureText(h.textContent).width, room: h.getBoundingClientRect().width - (bar > 0 ? 0 : 10) };
      });
      if (fit.word > fit.room)
        throw new Error(`заголовок «${title}» (${Math.round(fit.word)}px) не умещается рядом с полосой прокрутки: места ${Math.round(fit.room)}px`);
      for (const group of ['Документы', 'Люди и доступ', 'Наблюдение', 'Общее'])
        if (!(await panel(page).getByRole('heading', { level: 3, name: group }).count()))
          throw new Error(`в «Администрировании» нет группы «${group}»`);
      const width = await page.getByTestId('rail').evaluate(el => el.getBoundingClientRect().width);
      // Рейл 80 + подпанель 240, без черты (sharedui-md3-15).
      if (Math.abs(width - 320) > 2) throw new Error(`рейл с подпанелью шириной ${width}px, ждали 320`);
    });

    await check('shell-is-md3-top-app-bar-and-work-area', async () => {
      // Top app bar 64 на поверхности рамки, рабочая область — своя поверхность с углом 16, черт нет
      // ни у шапки, ни у рейла: их отделяет цвет и угол (sharedui-md3-15, [UI-79]).
      const look = await page.evaluate(() => {
        const css = sel => getComputedStyle(document.querySelector(sel));
        const header = document.querySelector('main > header');
        const search = document.querySelector('[data-testid=header-search]').getBoundingClientRect();
        return {
          header: header.getBoundingClientRect().height,
          headerBorder: css('main > header').borderBottomWidth,
          railBorder: css('[data-testid=rail]').borderRightWidth,
          panelBorder: css('[data-testid=section-panel]').borderLeftWidth,
          corner: css('[data-testid=work-area]').borderTopLeftRadius,
          search: { height: search.height, width: search.width },
        };
      });
      if (Math.abs(look.header - 64) > 1) throw new Error(`шапка высотой ${look.header}px, ждали 64`);
      for (const [name, w] of [['шапки', look.headerBorder], ['рейла', look.railBorder], ['подпанели', look.panelBorder]])
        if (w !== '0px') throw new Error(`у ${name} черта ${w}`);
      if (look.corner !== '16px') throw new Error(`у рабочей области угол ${look.corner}, ждали 16px`);
      if (Math.abs(look.search.height - 56) > 1 || look.search.width > 721)
        throw new Error(`строка поиска ${Math.round(look.search.width)}×${look.search.height}, ждали 56 и не шире 720`);
    });

    await check('rail-labels-fit-one-line', async () => {
      // Самая длинная подпись раздела — в одну строку без обрезки: рейл 80, а не 88.
      // Подпись — строчный span без переноса: перенос видно по высоте, вылет за рейл — по ширине.
      const bad = await page.locator('#app-rail-nav a > span:last-child').evaluateAll(spans => spans
        .filter(s => s.getBoundingClientRect().height > parseFloat(getComputedStyle(s).lineHeight) + 1
          || s.getBoundingClientRect().width > 80)
        .map(s => s.textContent));
      if (bad.length) throw new Error(`подпись не умещается в рейл 80: ${bad.join(', ')}`);
    });

    await check('section-panel-hides-and-lives-on-server', async () => {
      const hide = () => panel(page).getByRole('button', { name: 'Скрыть панель раздела' }).click();
      await toggled(page, 'collapsed', 'скрытие подпанели', hide);
      await panel(page).waitFor({ state: 'detached' });

      await page.reload();
      await settled(page);
      if (await panel(page).count()) throw new Error('после перезагрузки подпанель вернулась');

      // Свой раздел в рейле при скрытой подпанели — «покажи страницы», а не переход на первую.
      await toggled(page, 'expanded', 'щелчок по своему разделу в рейле', () => railSection(page, 'Система').click());
      await panel(page).waitFor();
      if (new URL(page.url()).pathname !== '/roles')
        throw new Error(`щелчок по своему разделу увёл на ${new URL(page.url()).pathname}`);

      // И кнопка у края страницы — второй вход к скрытой подпанели. Последняя запись тоже дождана:
      // следующая проверка уходит на другую страницу и читает выбор с сервера.
      await toggled(page, 'collapsed', 'повторное скрытие подпанели', hide);
      await panel(page).waitFor({ state: 'detached' });
      await toggled(page, 'expanded', 'кнопка «Показать панель раздела»',
        () => page.getByRole('button', { name: 'Показать панель раздела' }).click());
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
    await check.tidy('настройка подпанели не сброшена', () => callApiOk(page, 'PUT', '/account/settings', RESET));
    await page.evaluate(() => localStorage.removeItem('crg-rail'));
  }
}

const sideScroll = page => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

/**
 * Кнопки и ссылки оболочки на телефоне — одним сбором на обе проверки: цели касания и наложение в шапке.
 *
 * `small` — цели меньше 48 px: нижняя навигация, шапка, заголовок-меню (issue #1434, [UI-68]). Цель — рамка
 * вместе с `::after` утилиты `touch-target`: видимая кнопка 40, а нажимается 48. Мерить — при касании
 * (`emulateTouch`): без него утилита не включена, и замер видел бы рамку. Скрытая кнопка не цель: у неё
 * рамка 0×0, а `::after` всё равно посчитан бы — 8×8. Ссылки шапки (знак экземпляра) — тоже цели.
 * Отступлений нет (sharedui-md3-15): поиск в шапке — строка 56, и его `overflow-hidden`, срезающий
 * `::after`, порогу больше не мешает.
 *
 * `overlaps` — пары элементов шапки, чьи рамки пересекаются (`::after` в рамку не входит): строка поиска,
 * ужатая обёрткой до нуля, вылезала поверх центра активности, а ширина страницы при этом в порядке.
 */
const shellTargets = page => page.evaluate(() => {
  const px = v => parseFloat(v) || 0;
  const items = [...document.querySelectorAll('#app-bottom-nav a, #app-bottom-nav button, main > header button, main > header a, [data-testid=section-menu-trigger]')]
    .map(el => ({ el, box: el.getBoundingClientRect(), name: (el.getAttribute('aria-label') ?? el.textContent ?? '').trim().slice(0, 30) }))
    .filter(({ box }) => box.width > 0 && box.height > 0);
  const small = items.map(({ el, box, name }) => {
    const after = getComputedStyle(el, '::after');
    const grown = after.content !== 'none' && after.position === 'absolute';
    const width = grown ? box.width - px(after.left) - px(after.right) : box.width;
    const height = grown ? box.height - px(after.top) - px(after.bottom) : box.height;
    return width < 48 || height < 48 ? `«${name}» ${Math.round(width)}×${Math.round(height)}` : null;
  }).filter(Boolean);
  const header = items.filter(({ el }) => el.matches('main > header *'));
  const overlaps = [];
  for (let i = 0; i < header.length; i++)
    for (let j = i + 1; j < header.length; j++) {
      const a = header[i].box, b = header[j].box;
      if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 0.5 && Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 0.5)
        overlaps.push(`«${header[i].name}» и «${header[j].name}»`);
    }
  return { small, overlaps };
});

/**
 * Тесная шапка: длинное имя пространства и центр активности шире обычного (занят — кольцо, число,
 * значок). Стенд показывает одно состояние, а налезание зависит от ширины соседей поиска, — так оно
 * проверяется и там, где поиску достаётся меньше всего. Подмена — только в DOM и снимается тем же
 * вызовом с `false`.
 */
const crowdHeader = (page, on) => page.evaluate(on => {
  const name = document.querySelector('[data-testid=workspace-switcher] .truncate');
  const activity = document.querySelector('[data-testid=activity]');
  if (on) {
    if (name) { name.dataset.was = name.textContent; name.textContent = 'Электромонтаж и пусконаладка — все стройки'; }
    activity?.insertAdjacentHTML('beforeend', '<span data-crowd style="display:inline-block;width:40px"></span>');
  } else {
    if (name?.dataset.was !== undefined) { name.textContent = name.dataset.was; delete name.dataset.was; }
    document.querySelectorAll('[data-crowd]').forEach(el => el.remove());
  }
}, on);

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
      // Шапка — одной строкой и на телефоне (#1432): top app bar 64, как на рабочем столе (sharedui-md3-15).
      const header = await page.locator('main > header').boundingBox();
      if (!header || Math.round(header.height) !== 64) throw new Error(`шапка на 390 px высотой ${header?.height}px, а не 64 — не в одну строку`);
      // Гамбургера нет: разделы — внизу (#1434).
      if (await page.locator('main > header').getByRole('button', { name: 'Меню', exact: true }).count()) throw new Error('в шапке остался гамбургер');
      // Внизу у всех три места (work-03, Р0 — телефон Б), от числа разделов не зависит.
      const bar = page.getByTestId('bottom-nav');
      const places = (await bar.locator('a, button').allInnerTexts()).map(t => t.trim());
      if (places.join('|') !== 'Главная|Закреплённые|Меню') throw new Error(`внизу [${places}], ждали «Главная · Закреплённые · Меню»`);
      // Лист «Меню» — те же рабочие разделы, что в рейле рабочего стола, кроме Главной: она — местом строки.
      // Копия, разошедшаяся с рейлом, прятала бы разделы.
      await bar.getByRole('button', { name: 'Меню', exact: true }).click();
      const sheet = page.getByRole('dialog', { name: 'Меню' });
      await sheet.waitFor();
      // Заголовки РАЗДЕЛОВ листа, а не групп страниц внутри них («Отчёты и закрытие месяца»).
      const listed = (await sheet.locator('section > h3').allInnerTexts()).map(t => t.trim());
      // «Назад» закрывает лист, а не страницу под ним (#1418). Сначала закрыть, потом сравнивать: лист,
      // оставленный открытым, перекрыл бы страницу всем проверкам ниже.
      const before = page.url();
      await page.goBack();
      await sheet.waitFor({ state: 'detached' });
      if (page.url() !== before) throw new Error(`«Назад» с листа «Меню» увела на ${page.url()}`);
      const expected = work.filter(t => t !== 'Главная');
      if (listed.join('|') !== expected.join('|')) throw new Error(`в «Меню» [${listed}], в рейле рабочего стола [${expected}]`);
      for (const width of [390, 375]) {
        await page.setViewportSize({ width, height: 844 });
        const extra = await sideScroll(page);
        if (extra > 0) throw new Error(`на ${width} px каркас шире экрана на ${extra}px — страница едет вбок`);
        await emulateTouch(page);
        try {
          const { small, overlaps } = await shellTargets(page);
          if (small.length) throw new Error(`на ${width} px цели касания меньше 48 px: ${small.join(', ')}`);
          if (overlaps.length) throw new Error(`на ${width} px в шапке налезают: ${overlaps.join(', ')}`);
          await crowdHeader(page, true);
          try {
            const crowded = await shellTargets(page);
            if (crowded.overlaps.length) throw new Error(`на ${width} px в тесной шапке налезают: ${crowded.overlaps.join(', ')}`);
            if (crowded.small.length) throw new Error(`на ${width} px в тесной шапке цели меньше 48 px: ${crowded.small.join(', ')}`);
          } finally {
            await crowdHeader(page, false);
          }
        } finally {
          await emulateTouch(page, false);
        }
      }
      await page.setViewportSize({ width: 390, height: 844 });
    });

    // Страница из листа «Меню» открывается, лист за собой закрывается; «Закреплённые» открывают свой лист —
    // со списком или с подсказкой, как закрепить (work-03, work-04).
    await check('narrow-menu-sheet-opens-a-page-and-pinned-sheet-opens', async () => {
      const bar = page.getByTestId('bottom-nav');
      await bar.getByRole('button', { name: 'Меню', exact: true }).click();
      const sheet = page.getByRole('dialog', { name: 'Меню' });
      await sheet.waitFor();
      await sheet.getByRole('link', { name: 'Стройки', exact: true }).click();
      await sheet.waitFor({ state: 'detached' });
      await page.waitForURL(url => url.pathname === '/document-sets');
      await settled(page);
      if ((await bar.getByRole('button', { name: 'Меню', exact: true }).getAttribute('aria-current')) !== 'true'
        && (await bar.getByRole('button', { name: 'Закреплённые', exact: true }).getAttribute('aria-current')) !== 'true')
        throw new Error('на странице «Строек» не отмечено ни «Меню», ни «Закреплённые»');

      await bar.getByRole('button', { name: 'Закреплённые', exact: true }).click();
      const pinned = page.getByRole('dialog', { name: 'Закреплённые' });
      await pinned.waitFor();
      const links = await pinned.getByRole('link').count();
      const text = await pinned.innerText();
      if (!links && !text.includes('Закрепите страницу')) throw new Error(`лист «Закреплённые» пуст и без подсказки: «${text}»`);
      await page.keyboard.press('Escape');
      await pinned.waitFor({ state: 'detached' });
    });

    // Основное действие экрана на телефоне — плавающая кнопка с подписью (#1434): та же кнопка экрана
    // через `?do=`, а её двойник в шапке спрятан.
    await check('narrow-primary-action-is-a-labelled-fab', async () => {
      const fab = page.getByTestId('primary-fab');
      const label = (await fab.innerText()).trim();
      if (label !== 'Новая стройка') throw new Error(`плавающая кнопка строек — «${label}»`);
      // Extended FAB MD3 ([UI-68]): 56 px на primary-container, вид — классами компонента, без своего style.
      const look = await fab.evaluate(el => ({
        height: Math.round(el.getBoundingClientRect().height),
        style: el.getAttribute('style'),
        container: el.classList.contains('bg-primary-container'),
      }));
      if (look.height !== 56) throw new Error(`плавающая кнопка высотой ${look.height}px, а не 56`);
      if (look.style) throw new Error(`у плавающей кнопки свой style: ${look.style}`);
      if (!look.container) throw new Error('плавающая кнопка не на primary-container');
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
      // Вопрос — своё окно над окном ([UI-75]): ищется по странице, а не внутри окна.
      const ask = page.getByRole('alertdialog', { name: 'Закрыть без сохранения?' });
      await ask.waitFor();
      stayed('окна с вводом');
      await page.goBack();
      await ask.waitFor({ state: 'detached' });
      if (!(await dialog.isVisible())) throw new Error('«Назад» при вопросе закрыла окно с набранным');
      stayed('вопроса');
      await page.goBack();
      await ask.waitFor();
      await ask.getByRole('button', { name: 'Закрыть', exact: true }).click();
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
