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
//   • гард несохранённых правок перехватывает переход по сайдбару.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных (хотя бы одна стройка,
// хотя бы один тип поля) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/routing-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, clearSession, createChecks } from './harness.mjs';

const USER_EMAIL = process.env.SMOKE_USER_EMAIL || 'petrov@bhs.local';
const USER_PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const PALETTE_INPUT = 'input[placeholder="Перейти к разделу…"]';

const { check, summarize } = createChecks();
const pathOf = page => new URL(page.url()).pathname;
const atPath = p => url => new URL(url).pathname === p;

const browser = await launchBrowser();
const page = await browser.newPage();
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
    await page.getByRole('link', { name: 'Наборы данных', exact: true }).click();
    await page.waitForURL(atPath('/datasets'), { timeout: 5000 });
    // Ждём атрибут, а не читаем сразу: адрес меняется раньше, чем React дорисует активный пункт.
    await page.waitForSelector('nav a[href="/datasets"][aria-current="page"]', { timeout: 5000 });
    const others = await page.locator('nav a[aria-current="page"]').count();
    if (others !== 1) throw new Error(`текущим помечен не один пункт, а ${others}`);
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
    await page.waitForURL(u => /^\/document-sets\/[0-9a-f-]{36}$/.test(new URL(u).pathname), { timeout: 5000 });
    constructionUrl = page.url();
    await assertConstructionScreen();
  });
  await check('nested-route-cold-load', async () => {
    await page.goto(constructionUrl);
    await page.waitForLoadState('networkidle');
    if (pathOf(page) !== new URL(constructionUrl).pathname) throw new Error('холодная загрузка увела с адреса');
    await assertConstructionScreen();
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

  // ── AdminRoute уводит не-администратора ────────────────────────────────────
  const userContext = await browser.newContext();
  const userPage = await userContext.newPage();
  await check('admin-route-blocks-user', async () => {
    await login(userPage, USER_EMAIL, USER_PASSWORD);
    await userPage.goto(`${BASE}/settings`);
    await userPage.waitForURL(atPath('/document-sets'), { timeout: 5000 });
  });
  await userContext.close();
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Маршрутный smoke');
