// Маршрутный smoke-тест (issue #694).
// Маршрутизация не покрыта юнит-тестами, а живёт на внешней библиотеке, которая обновляется
// мажорными версиями. Этот прогон проверяет по живому фронту то, что ломается при таком
// обновлении в первую очередь:
//   • редирект неаутентифицированного на /login и вход на рабочую область;
//   • NavLink: переход и признак активного пункта;
//   • вложенные Routes (`document-sets/*`) и параметры пути при холодной загрузке;
//   • useSearchParams: query действительно читается страницей;
//   • история браузера: назад/вперёд;
//   • useNavigate из командной палитры;
//   • AdminRoute уводит не-администратора;
//   • гард несохранённых правок перехватывает переход по сайдбару;
//   • журнал действий открывается администратору и закрыт правом для остальных (issue #950);
//   • редактор ролей объясняет каждую галку и закрыт тем же правом, что и пользователи (#951);
//   • роль «все права» показана нередактируемой, а новому пользователю роль не подставляется
//     умолчанием — обе регрессии ревью нашло уже в master (#983).
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных (хотя бы одна стройка,
// хотя бы один тип поля) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/routing-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, clearSession, createChecks, watchRequests, settled, callApi, registryAddress,
  railSection, menuText, wholeMenu }
  from './harness.mjs';
import { TABLE_SEED } from './seed-invoices.mjs';

const USER_EMAIL = process.env.SMOKE_USER_EMAIL || 'petrov@bhs.local';
const USER_PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const PALETTE_INPUT = 'input[aria-label="Поиск и действия"]';

const { check, summarize } = createChecks();
const pathOf = page => new URL(page.url()).pathname;
const atPath = p => url => new URL(url).pathname === p;

const browser = await launchBrowser();
const page = await browser.newPage();
// Наблюдение за запросами обычно ставит сам login(). Здесь — отдельно и заранее: первые проверки
// ходят по страницам ДО входа, а запрос, ушедший раньше подписки, для settled() не существует.
await watchRequests(page);
page.on('dialog', d => d.accept());

try {
  // ── Защищённый маршрут: без токена уводит на вход ───────────────────────────
  await page.goto(`${BASE}/login`);
  await clearSession(page);
  const historyBefore = await page.evaluate(() => history.length);
  await check('protected-redirects-to-login', async () => {
    // Считаем 401: на вход должен увести сам маршрут, а не отлуп сервера на первом запросе
    // (иначе проверка проходит и при вовсе снятом ProtectedRoute).
    let unauthorized = 0;
    const countUnauthorized = r => { if (r.status() === 401) unauthorized++; };
    page.on('response', countUnauthorized);
    try {
      await page.goto(`${BASE}/datasets`);
      await page.waitForURL(atPath('/login'), { timeout: 5000 });
      if (unauthorized) throw new Error(`закрытый экран успел сходить на сервер и получить 401 (${unauthorized})`);
    } finally { page.off('response', countUnauthorized); }
  });
  // Редирект сделан с replace. Проверяем длину истории, а не «назад»: при push «назад»
  // возвращает на закрытый адрес, тот немедленно редиректит обратно — и по адресу строки
  // разницы не видно, хотя пользователь заперт в цикле /datasets → /login → /datasets.
  await check('login-redirect-replaces-history', async () => {
    const after = await page.evaluate(() => history.length);
    if (after !== historyBefore + 1)
      throw new Error(`редирект добавил запись в историю (было ${historyBefore}, стало ${after})`);
  });

  // ── Вход ведёт на рабочую область ───────────────────────────────────────────
  await check('login-lands-on-workspace', async () => {
    await login(page);
    await page.waitForURL(atPath('/document-sets'), { timeout: 10000 });
  });

  // ── NavLink: переход и активный пункт ───────────────────────────────────────
  await check('navlink-navigates-and-marks-active', async () => {
    // Страницы раздела — в его подпанели (issue #1429): сперва раздел рейла, он ведёт на первую.
    await railSection(page, 'Данные').click();
    await page.waitForURL(atPath('/common-data'), { timeout: 5000 });
    await page.getByTestId('section-panel').getByRole('link', { name: 'Наборы данных', exact: true }).click();
    await page.waitForURL(atPath('/datasets'), { timeout: 5000 });
    // Ждём атрибут, а не читаем сразу: адрес меняется раньше, чем React дорисует активный пункт.
    await page.waitForSelector('nav a[href="/datasets"][aria-current="page"]', { timeout: 5000 });
    const others = await page.locator('nav a[aria-current="page"]').count();
    if (others !== 1) throw new Error(`текущим помечен не один пункт, а ${others}`);
    // Раздел в рейле помечен своим «текущий раздел», а не «текущая страница»: иначе страниц было бы две.
    const section = await page.locator('#app-rail-nav a[aria-current="true"]').innerText();
    if (section.trim() !== 'Данные') throw new Error(`в рейле текущим помечен раздел «${section.trim()}»`);
  });

  // ── История браузера ────────────────────────────────────────────────────────
  await check('history-back-forward', async () => {
    await page.getByRole('link', { name: 'Документы качества' }).click();
    await page.waitForURL(atPath('/quality-docs'), { timeout: 5000 });
    await page.goBack();
    await page.waitForURL(atPath('/datasets'), { timeout: 5000 });
    await page.goForward();
    await page.waitForURL(atPath('/quality-docs'), { timeout: 5000 });
  });

  // ── Вложенные Routes и параметры пути ───────────────────────────────────────
  // `/document-sets/*` монтирует собственный <Routes>; проверяем и переход по клику,
  // и холодную загрузку того же адреса — параметр берётся из URL, а не из состояния.
  let constructionUrl = '';
  let defaultPanel = 'catalog';
  const firstPanel = async () => (await callApi(page, 'GET', '/account/access')).body.modules
    .some(m => m.code === 'plan' && m.available) ? 'estimates' : 'catalog';
  // Экран стройки узнаётся по своей кнопке; заодно убеждаемся, что список строек сменился,
  // а не остался под изменившимся адресом (при непопадании в маршрут не будет ни того ни другого).
  const assertConstructionScreen = async () => {
    await page.getByRole('button', { name: 'Добавить раздел' }).first()
      .waitFor({ state: 'visible', timeout: 5000 });
    if (await page.getByRole('button', { name: 'Новая стройка' }).count())
      throw new Error('остался список строек — вложенный маршрут не сработал');
  };
  await check('nested-route-params', async () => {
    await page.goto(`${BASE}/document-sets`);
    await page.waitForLoadState('networkidle');
    const card = page.locator('h3').first();
    if (!(await card.count())) throw new Error('нет ни одной стройки — нужны демо-данные');
    await card.click();
    // Голый адрес стройки уводит на панель по умолчанию (plan-02) — первую блока «Работы», а без него
    // «Каталог». Блок у администратора — «Сметы», если включён модуль plan (в CI включён с plan-09).
    defaultPanel = await firstPanel();
    await page.waitForURL(u => new RegExp(`^/document-sets/[0-9a-f-]{36}/${defaultPanel}$`).test(new URL(u).pathname), { timeout: 5000 });
    constructionUrl = page.url();
    await assertConstructionScreen();
  });
  await check('nested-route-cold-load', async () => {
    await page.goto(constructionUrl);
    await page.waitForLoadState('networkidle');
    if (pathOf(page) !== new URL(constructionUrl).pathname) throw new Error('холодная загрузка увела с адреса');
    await assertConstructionScreen();
  });

  // ── Панели карточки стройки и раздела: прежние адреса, /catalog, голый адрес (plan-02) ──
  //
  // Панели переехали на общий реестр (`constructionPanels.ts`), маршрут — на `:panel/*`. Закладки
  // обязаны открываться как раньше: каждая панель своим адресом и активным пунктом, голый адрес
  // стройки и раздела — панелью по умолчанию, с ЗАМЕНОЙ: «назад» с неё не возвращает на адрес,
  // который тут же перенаправит обратно.
  await check('construction-panel-addresses-open-as-before', async () => {
    const site = (await callApi(page, 'GET', '/constructions')).body.find(c => c.sections?.length)
      ?? (await callApi(page, 'GET', '/constructions')).body[0];
    const full = (await callApi(page, 'GET', `/constructions/${site.id}`)).body;
    const section = full.sections?.[0];
    if (!section) throw new Error('у строек нет ни одного раздела — нужны демо-данные');
    const levels = [
      [`/document-sets/${site.id}`, 'Эта стройка'],
      [`/document-sets/${site.id}/sections/${section.id}`, 'Этот раздел'],
    ];
    for (const [base, block] of levels) {
      for (const [panel, label] of [['catalog', 'Каталог'], ['datasets', 'Наборы данных'], ['subscribers', 'Подписчики']]) {
        await page.goto(`${BASE}${base}/${panel}`);
        await settled(page);
        if (pathOf(page) !== `${base}/${panel}`) throw new Error(`${base}/${panel} увёл на ${pathOf(page)}`);
        const active = page.locator('button[aria-current="true"]', { hasText: label });
        await active.first().waitFor({ timeout: 5000 }).catch(() => {
          throw new Error(`на ${base}/${panel} пункт «${label}» блока «${block}» не помечен открытым`);
        });
      }
      await page.goto(`${BASE}/document-sets`);
      await settled(page);
      const before = await page.evaluate(() => history.length);
      await page.goto(`${BASE}${base}`);
      await page.waitForURL(atPath(`${base}/${defaultPanel}`), { timeout: 5000 });
      // goto добавляет одну запись; перенаправление с заменой — ни одной.
      const after = await page.evaluate(() => history.length);
      if (after !== before + 1) throw new Error(`перенаправление ${base} добавило запись в историю (${before} → ${after})`);
    }
  });

  // ── useSearchParams: query читается страницей ───────────────────────────────
  // Страница подтверждения адреса без email/token не ходит на сервер вовсе, а с ними —
  // ходит. Считаем запросы, а не текст: сообщение об ошибке у обоих случаев одно и то же,
  // и по нему нельзя отличить «параметры не дочитались» от «сервер их отверг».
  await check('search-params-are-read', async () => {
    let calls = 0;
    const count = r => { if (r.url().includes('/api/auth/confirm-email')) calls++; };
    page.on('request', count);
    try {
      await page.goto(`${BASE}/confirm-email`);
      await page.getByText('Ссылка недействительна или устарела.').waitFor({ state: 'visible', timeout: 5000 });
      if (calls) throw new Error('без параметров страница всё равно пошла на сервер');
      await page.goto(`${BASE}/confirm-email?email=nobody%40bhs.local&token=bogus`);
      await page.waitForFunction(() => !document.body.innerText.includes('Подтверждаем…'), { timeout: 10000 });
      if (calls !== 1) throw new Error(`параметры запроса не дошли до страницы (запросов: ${calls})`);
    } finally { page.off('request', count); }
  });

  // ── useNavigate: командная палитра ──────────────────────────────────────────
  await check('palette-navigates', async () => {
    await page.goto(`${BASE}/document-sets`);
    await page.waitForLoadState('networkidle');
    await page.keyboard.press('Control+k');
    await page.waitForSelector(PALETTE_INPUT, { state: 'visible', timeout: 3000 });
    await page.fill(PALETTE_INPUT, 'Шаблоны');
    await page.keyboard.press('Enter');
    await page.waitForURL(atPath('/templates'), { timeout: 5000 });
  });

  // ── Гард несохранённых правок перехватывает переход по сайдбару ─────────────
  await check('leave-guard-intercepts-nav', async () => {
    await page.goto(`${BASE}/field-types`);
    await page.waitForLoadState('networkidle');
    // Первый тип в списке открыт по умолчанию; правим описание — оно ни на что не влияет,
    // а уходим потом через «Не сохранять», так что данные остаются нетронутыми.
    const field = page.getByLabel('Описание');
    if (!(await field.count())) throw new Error('нет ни одного типа поля — нужны демо-данные');
    await field.fill('правка для проверки гарда');
    await page.getByRole('link', { name: 'Стройки', exact: true }).click();
    await page.getByRole('dialog').getByText('Есть несохранённые изменения', { exact: false })
      .waitFor({ state: 'visible', timeout: 5000 });
    if (pathOf(page) !== '/field-types') throw new Error('переход состоялся, хотя гард показал диалог');
    await page.getByRole('button', { name: 'Не сохранять' }).click();
    await page.waitForURL(atPath('/document-sets'), { timeout: 5000 });
  });

  // ── …и переход командой палитры (issue #1238) ───────────────────────────────
  // Палитра звала переход напрямую, мимо охраны: правки терялись за два нажатия, без вопроса.
  await check('leave-guard-intercepts-palette', async () => {
    await page.goto(`${BASE}/field-types`);
    await page.waitForLoadState('networkidle');
    await page.getByLabel('Описание').fill('правка для проверки гарда');
    await page.keyboard.press('Control+k');
    await page.fill(PALETTE_INPUT, 'Шаблоны');
    await page.keyboard.press('Enter');
    await page.getByRole('dialog').getByText('Есть несохранённые изменения', { exact: false })
      .waitFor({ state: 'visible', timeout: 5000 });
    if (pathOf(page) !== '/field-types') throw new Error('палитра увела со страницы, хотя гард показал диалог');
    if (await page.locator(PALETTE_INPUT).count()) throw new Error('палитра осталась открытой поверх вопроса');
    await page.getByRole('button', { name: 'Не сохранять' }).click();
    await page.waitForURL(atPath('/templates'), { timeout: 5000 });
  });

  // ── Закрытый раздел отвечает страницей, а не переносом ─────────────────────
  //
  // Прежняя редакция ждала, что пользователя УВЕДЁТ на список строек, и проверяла смену адреса.
  // Ровно это требование AUTH-15 и запрещает: молчаливое перенаправление неотличимо от поломки —
  // человек нажал пункт, оказался не там и не знает, сломалась система или ему нельзя. С issue
  // #952 поведение сменилось, и проверка упала — как и должна была.
  // ── Журнал действий: экран открывается и показывает записи (issue #950) ─────
  //
  // Проверка не про маршрут, а про то, ради чего экран заведён: пустая таблица на нём выглядит
  // исправно работающим журналом, и отличить «ничего не происходило» от «записи не доходят» можно
  // только по конкретной записи. Берём ту, что есть на любом экземпляре всегда, — состав модулей.
  //
  // ⚠️ Запись ищется ОТБОРОМ, а не глазами на первой странице. Прежняя редакция полагала, будто
  // «её пишет сам запуск», и смотрела текст экрана целиком. Пишется она при СМЕНЕ состава, а не при
  // каждом старте: на накопленной базе она от позапрошлого дня и лежит под четырьмя сотнями записей,
  // то есть на первую страницу не попадёт больше никогда. Свежая база CI давала её первой строкой —
  // поэтому проверка была зелёной ровно там, где на неё никто не смотрит, и красной на стенде, где
  // её читают как поломку журнала (issue #1120).
  //
  // Побочная выгода: отбор по действию до сих пор не проверял никто, а теперь без него не обойтись.
  await check('activity-log-shows-records', async () => {
    await page.goto(`${BASE}/activity`);
    await page.waitForSelector('text=Журнал действий', { timeout: 5000 });
    // Пункт в меню у того, кому можно, — вторая половина проверки ниже: «не видно никому» и
    // «видно всем» одинаково проходят, пока смотришь одним глазом.
    const menu = await menuText(page);
    if (!menu.includes('Журнал действий'))
      throw new Error('пункта «Журнал действий» нет в меню у администратора');

    // Отбор адресуется своим aria-label: видимой подписи у него нет, а «список» на этом экране
    // один, но завтра их может стать двое.
    await page.getByRole('combobox', { name: 'Отбор по действию' }).click();
    const option = page.getByRole('option', { name: 'Изменён состав модулей' });
    if (!(await option.count()))
      throw new Error('в отборе нет действия «Изменён состав модулей» — его не отдаёт /activity/actions');
    // Ждём ОТВЕТ отобранного запроса, а не время: иначе на экране остаются прежние записи, и
    // утверждения ниже читали бы их — то есть проходили бы, ничего не отобрав.
    const answered = page.waitForResponse(
      r => r.request().method() === 'GET' && /\/api\/activity\?.*action=core\.modules\.changed/.test(r.url()),
      { timeout: 15_000 });
    await option.first().click();
    const answer = await answered.catch(e => {
      // Своё сообщение — ТОЛЬКО на истечение срока, и оно называет причину: выбранное действие не
      // уехало в запрос. Без этого отказ выглядел бы сырым таймаутом Playwright, по которому не
      // видно, что сломался именно отбор.
      if (e?.name === 'TimeoutError')
        throw new Error('экран не отправил отобранный запрос — выбор действия до API не доехал');
      throw e;
    });
    if (!answer.ok()) throw new Error(`отбор журнала отказал: ${answer.status()}`);
    await page.waitForFunction(() => !/Загрузка\.\.\./.test(document.body.innerText),
      null, { timeout: 10_000 }).catch(() => {});

    const t = await page.locator('body').innerText();
    if (/Записей нет/.test(t))
      throw new Error('записей о составе модулей нет вовсе — их пишет смена состава, значит она не доходит');
    if (!/Изменён состав модулей/.test(t))
      throw new Error(`отбор не показал записи отобранного действия: ${t.slice(0, 400)}`);
    if (!/Система/.test(t))
      throw new Error('у записи не показан автор');
  });

  // ── Редактор ролей: экран открывается и объясняет каждую галку (issue #951) ─
  //
  // Проверяется не «страница нарисовалась», а условие, без которого редактор небезопасен (AUTH-5):
  // права сгруппированы по модулям и у каждой галки стоит объяснение. Экран с одними кодами прав
  // выглядит работающим и означает раздачу доступа вслепую.
  await check('roles-editor-explains-every-permission', async () => {
    await page.goto(`${BASE}/roles`);
    await page.waitForSelector('text=Роли и права', { timeout: 5000 });
    // Заголовок на месте раньше данных: роли и справочник прав приходят своими запросами.
    await settled(page);

    // Не `nav` целиком: на этом экране есть и левая колонка списка ролей.
    const menu = await menuText(page);
    if (!menu.includes('Роли и права'))
      throw new Error('пункта «Роли и права» нет в меню у администратора');

    const t = await page.locator('body').innerText();
    for (const role of ['Администратор', 'Бухгалтер', 'Инженер ИД']) {
      if (!t.includes(role)) throw new Error(`в списке ролей нет «${role}»`);
    }
    // Заголовок группы рисуется капителью (CSS text-transform), поэтому сверяем без учёта
    // регистра: иначе проверка ловила бы оформление, а не группировку.
    if (!/ядро/i.test(t)) throw new Error('права не сгруппированы по модулям');
    if (!t.includes('Открывает доступ:'))
      throw new Error('у прав нет объяснения — редактор раздаёт доступ вслепую');

    // Галок столько же, сколько прав в справочнике: пустая матрица прошла бы все проверки выше.
    const boxes = await page.locator('input[type=checkbox]').count();
    if (boxes < 10) throw new Error(`галок на экране ${boxes} — матрица прав не построилась`);
  });

  // Роль «все права» открывается первой по алфавиту, и её состав сервер править отказывается.
  // Экран обязан сказать это ДО щелчка: галки, которые щёлкаются и не сохраняются, — это запрет,
  // о котором узнают, только нарушив его (ревью #983).
  await check('all-permissions-role-is-visibly-read-only', async () => {
    await page.goto(`${BASE}/roles`);
    await page.waitForSelector('text=Состав этой роли не правится', { timeout: 5000 });

    // ⚠️ Сперва ПОЛОЖИТЕЛЬНОЕ утверждение, и только потом запреты. Баннер приходит ответом
    // /roles, а галки — отдельным ответом /roles/permissions: пока справочник не ответил, живых
    // галок ноль и кнопки нет — оба запрета ниже «выполнены», а проверять нечего (ревью #986).
    const boxes = page.locator('input[type=checkbox]');
    await boxes.nth(9).waitFor({ timeout: 5000 });

    const enabled = await page.locator('input[type=checkbox]:not([disabled])').count();
    if (enabled > 0)
      throw new Error(`у роли «все права» ${enabled} живых галок — сервер откажет на сохранении`);

    // Галки не просто мертвы — они отмечены: состав такой роли равен справочнику целиком.
    const ticked = await page.locator('input[type=checkbox]:checked').count();
    if (ticked < 10)
      throw new Error(`у роли «все права» отмечено ${ticked} галок — состав показан неполным`);

    if (await page.locator('text=Сохранить состав прав').count())
      throw new Error('у роли «все права» предлагается сохранить состав, которого она не имеет');
  });

  // Роль новому пользователю НЕ подставляется. Проверка живая и стоит отдельно, потому что
  // подставлялась она молча: диалог брал первую роль списка, сервер отдавал список по
  // техническому имени, и сотрудник заводился «Бухгалтером» — с правом отмечать оплату и
  // закрывать период (ревью #983). Типы этого не видят, а на экране не видно вовсе.
  await check('new-user-role-is-never-defaulted', async () => {
    await page.goto(`${BASE}/users`);
    await page.click('text=Добавить пользователя');
    await page.waitForSelector('text=Новый пользователь', { timeout: 5000 });

    const dialog = page.locator('[role=dialog]');

    // ⚠️ Сперва убеждаемся, что роли ЕСТЬ. На пустом списке подставлять нечего: подпись «Без ролей»
    // на месте, кнопка выключена — оба утверждения ниже прошли бы, не коснувшись регрессии, которую
    // проверка заведена ловить (ревью #986). Меню открываем — заодно видно, что список живой.
    //
    // С #984 роль выбирается меню с ГАЛКАМИ (их может быть несколько), а не выпадающим списком:
    // пункты объявлены как menuitemcheckbox, и «не выбрано» читается по ним, а не по placeholder.
    await dialog.locator('button[aria-label="Роли нового пользователя"]').click();
    await page.waitForSelector('[role=menuitemcheckbox]', { timeout: 5000 });
    for (const role of ['Администратор', 'Инженер ИД']) {
      if (!(await page.locator(`[role=menuitemcheckbox]:has-text("${role}")`).count()))
        throw new Error(`в меню ролей диалога нет «${role}» — подставлять было бы нечего`);
    }
    const checked = await page.locator('[role=menuitemcheckbox][aria-checked=true]').count();
    if (checked > 0)
      throw new Error(`роль в диалоге отмечена заранее (${checked}) — выдача прав идёт по умолчанию`);
    await page.keyboard.press('Escape');

    // Меню закрыто, и подпись кнопки говорит то же самое словами.
    const text = await dialog.innerText();
    if (!text.includes('Без ролей'))
      throw new Error('диалог не говорит, что роль не выбрана');

    const create = dialog.locator('button', { hasText: 'Создать' }).last();
    if (await create.isEnabled())
      throw new Error('пользователя можно завести, не выбрав роль');

    await page.keyboard.press('Escape');
  });

  const userContext = await browser.newContext();
  const userPage = await userContext.newPage();
  await check('closed-section-answers-with-a-page', async () => {
    await login(userPage, USER_EMAIL, USER_PASSWORD);
    await userPage.goto(`${BASE}/settings`);

    // Адрес НЕ меняется: человек остался там, куда шёл.
    await userPage.waitForSelector('text=недоступен', { timeout: 5000 });
    const url = new URL(userPage.url());
    if (url.pathname !== '/settings')
      throw new Error(`адрес сменился на ${url.pathname} — это и есть запрещённое перенаправление`);

    // И страница называет, ЧЕГО не хватает: без кода права разговор с администратором
    // превращается в угадайку.
    await userPage.waitForSelector('text=core.system.manage', { timeout: 5000 });
  });

  // Пункта закрытого раздела нет и в меню (AUTH-16): спрятать раздел и оставить пункт значило бы
  // показывать дверь, которая отвечает отказом.
  await check('closed-section-is-absent-from-the-menu', async () => {
    await userPage.goto(`${BASE}/document-sets`);
    await userPage.waitForSelector('nav', { timeout: 5000 });
    // Всё меню — обходом разделов: подпанель на экране одна, и пункт в чужом разделе она не видит.
    const menu = await wholeMenu(userPage);
    if (menu.includes('Параметры системы'))
      throw new Error('пункт «Параметры системы» виден пользователю, у которого нет права на него');
    if (menu.includes('Журнал действий'))
      throw new Error('пункт «Журнал действий» виден тому, у кого нет права его читать');
    if (menu.includes('Роли и права'))
      throw new Error('пункт «Роли и права» виден тому, кто не управляет пользователями');
  });

  // Редактор ролей закрыт правом, а не отсутствием пункта в меню: адрес набирают руками.
  await check('roles-editor-is-closed-by-a-right', async () => {
    await userPage.goto(`${BASE}/roles`);
    await userPage.waitForSelector('text=недоступен', { timeout: 5000 });
    await userPage.waitForSelector('text=core.users.manage', { timeout: 5000 });
  });

  // Журнал закрыт правом, а не тем, что пункта нет в меню: адрес набирают руками и открывают по
  // ссылке (issue #950, ТЗ CORE-28).
  await check('activity-log-is-closed-by-a-right', async () => {
    await userPage.goto(`${BASE}/activity`);
    await userPage.waitForSelector('text=недоступен', { timeout: 5000 });
    await userPage.waitForSelector('text=core.audit.read', { timeout: 5000 });
  });

  // ── Человек без модуля счетов не находит счёт ни одним путём этапа 2 (H1, issue #1104) ──
  //
  // Суммы закрыты МЕСТОМ ХРАНЕНИЯ (счёт лежит в таблице модуля, общими путями его не прочитать), и
  // держат это сторожа сборки. Здесь — живое подтверждение по путям, которые этап добавил: экран
  // счетов, таблица модуля и её реестр, диалог закрытия периода с историей, журнал действий. XLSX и
  // загрузки 1С в модуле ещё нет — приедут со своими задачами и встанут сюда же.
  //
  // ⚠️ Под тем же пользователем, что и проверки выше, и без второго входа: входы прогонов считаны.
  await check('outsider-finds-no-invoice-by-any-stage-path', async () => {
    const access = await callApi(userPage, 'GET', '/account/access');
    const own = (access.body?.permissions ?? []).filter(code => code.startsWith('costs.'));
    if (access.status !== 200 || own.length)
      throw new Error(`у пользователя есть права модуля счетов (${own.join(', ') || access.status}) — `
        + 'проверять «без модуля» не на ком');

    // Каждый путь отвечает отказом, а не пустым списком: пустой список не отличить от «счетов нет».
    const doors = [
      ['GET', '/costs/invoices'],
      ['GET', '/tables/costs.invoices/columns'],
      ['GET', '/periods/history'],
      ['POST', '/periods/close/preview', { contour: 'Company', from: '2026-09-01', through: '2026-09-30' }],
      ['GET', '/activity'],
    ];
    for (const [method, path, body] of doors) {
      const answer = await callApi(userPage, method, path, body);
      if (answer.status !== 403)
        throw new Error(`${method} ${path} ответил ${answer.status}, а не отказом: `
          + String(JSON.stringify(answer.body)).slice(0, 200));
    }

    // Открытый ему путь — системные наборы данных — таблицу счетов не предлагает и посеянных счетов
    // не называет: таблица модуля как набор отдаётся правами читающего.
    const sets = await callApi(userPage, 'GET', '/datasets/system-candidates?scope=System');
    if (sets.status !== 200) throw new Error(`системные наборы не прочитаны: ${sets.status}`);
    const offered = JSON.stringify(sets.body);
    for (const sign of ['costs.invoices', TABLE_SEED.purpose])
      if (offered.includes(sign))
        throw new Error(`среди системных наборов человеку без модуля виден «${sign}»`);

    // И экраны: отказ страницей, без строк.
    for (const address of [`${BASE}/invoices`, registryAddress({ type: 'condition', column: 'Номер', op: 'eq', value: 'x' })]) {
      await userPage.goto('about:blank');
      await userPage.goto(address);
      await settled(userPage);
      const text = await userPage.locator('body').innerText();
      if (!/недоступ|нет доступа|нет права/i.test(text))
        throw new Error(`экран ${new URL(address).pathname} не отвечает отказом: ${text.slice(0, 200)}`);
      if (text.includes(TABLE_SEED.purpose))
        throw new Error(`на экране ${new URL(address).pathname} видны посеянные счета`);
    }
  });
  // ── Вход и пункты меню под каждой системной ролью (plan-02, ТЗ AUTH-16.1, AUTH-15) ──
  //
  // `/` ведёт на страницу, вычисленную по доступу (`entryPage.ts`), а не на «Стройки» всем. Под
  // каждой системной ролью он обязан привести на видимую страницу, а каждый пункт меню — открыться
  // без отказа: пункт, который виден и отвечает 403, — худший из ответов.
  //
  // ⚠️ Без новых входов: входы прогонов считаны (`suites.test.mjs`), и лишних в пределе частоты нет.
  // Роли меняет администратор той же учётной записи, под которой открыта страница пользователя:
  // смена ролей обновляет отметку безопасности, клиент молча меняет токен по refresh и получает
  // новые права (AUTH-7) — перелогин не нужен. Прежние роли возвращаются в любом случае: наборы
  // после этого ходят под этой же учётной записью.
  await check('every-system-role-enters-a-visible-page', async () => {
    const roles = (await callApi(page, 'GET', '/roles')).body.filter(r => r.system);
    // Список читается у сервера, а не записан здесь: новая системная роль попадает в проход сама.
    // Нижняя граница — чтобы проход не прошёл на пустом списке.
    if (roles.length < 9) throw new Error(`системных ролей ${roles.length} — проход проверял бы не всех`);
    const users = (await callApi(page, 'GET', '/users')).body;
    const me = (Array.isArray(users) ? users : users.items).find(u => u.email.toLowerCase() === USER_EMAIL.toLowerCase());
    const before = me.roles.map(r => r.name);
    const problems = [];
    try {
      for (const role of roles) {
        const set = await callApi(page, 'PUT', `/users/${me.id}/roles`, { roles: [role.name] });
        if (set.status !== 200) throw new Error(`роль «${role.title}» не выдана: ${set.status}`);
        problems.push(...await walkAs(userPage, role.title));
      }
    } catch (e) {
      problems.push(e.message);
    }
    // Возврат — с проверкой ответа: не вернулся — наборы после этого идут под последней ролью прохода и
    // краснеют далеко от причины. Поэтому он назван первым, громче находок прохода.
    const back = await callApi(page, 'PUT', `/users/${me.id}/roles`, { roles: before });
    if (back.status !== 200)
      problems.unshift(`ПРЕЖНИЕ РОЛИ (${before.join(', ')}) НЕ ВОЗВРАЩЕНЫ: ${back.status} — следующие наборы под ${USER_EMAIL} идут не под своей ролью`);
    if (problems.length) throw new Error(problems.join('\n'));
  });
  await userContext.close();
} finally {
  await browser.close();
}

/**
 * Вход и все пункты меню под текущими правами страницы: `/` ведёт на видимую страницу, каждый пункт
 * открывается без отказа страницей и без ответа 403. Возвращает найденные расхождения, а не падает
 * на первом: одна неверно закрытая дверь видна у многих ролей, и список «где именно» дешевле
 * повторных кругов.
 */
async function walkAs(p, title) {
  const problems = [];
  const denied = [];
  const on403 = r => { if (r.status() === 403) denied.push(`${r.request().method()} ${new URL(r.url()).pathname}`); };
  const refused = async () => /Раздел .* недоступен|Нет доступных разделов|Для него нужн/.test(await p.locator('body').innerText());
  p.on('response', on403);
  try {
    await p.goto(`${BASE}/`);
    await p.waitForURL(u => new URL(u).pathname !== '/', { timeout: 10_000 }).catch(() => {});
    await settled(p);
    const landed = pathOf(p);
    if (landed === '/') problems.push(`«${title}»: адрес «/» никуда не привёл`);
    else if (await refused()) problems.push(`«${title}»: вход привёл на отказ (${landed})`);

    // Пункты — обходом рейла: подпанель на экране одна.
    const rail = p.getByRole('navigation', { name: 'Разделы' });
    const names = (await rail.getByRole('link').allInnerTexts()).map(t => t.trim()).filter(Boolean);
    const hrefs = new Set();
    for (const name of names) {
      await railSection(p, name).click();
      await rail.locator('a[aria-current="true"]', { hasText: name }).waitFor({ timeout: 5000 });
      const panel = p.getByTestId('section-panel');
      await panel.waitFor({ timeout: 5000 });
      for (const href of await panel.locator('a[href^="/"]').evaluateAll(as => as.map(a => a.getAttribute('href'))))
        hrefs.add(href);
    }
    if (!hrefs.size) problems.push(`«${title}»: в меню нет ни одного пункта`);
    for (const href of hrefs) {
      denied.length = 0;
      await p.goto(`${BASE}${href}`);
      await settled(p);
      if (await refused()) problems.push(`«${title}»: пункт ${href} отвечает отказом страницей`);
      if (denied.length) problems.push(`«${title}»: на ${href} отказ 403 — ${[...new Set(denied)].join(', ')}`);
    }
  } finally {
    p.off('response', on403);
  }
  return problems;
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Маршрутный smoke');
