// Общая обвязка живых smoke-прогонов: где взять Playwright и браузер, как войти,
// как считать проверки. Используется keyboard-smoke.mjs и routing-smoke.mjs.
//
// Playwright лежит в npx-кеше (не в node_modules пакета), браузер — в ms-playwright;
// оба пути резолвятся динамически и переопределяются env PLAYWRIGHT_PKG / CHROMIUM_EXE.

import { readdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const BASE = process.env.SMOKE_BASE || 'http://localhost:5173';
export const EMAIL = process.env.SMOKE_EMAIL || 'admin@bhs.local';
export const PASSWORD = process.env.SMOKE_PASSWORD || 'Demo12345!';

/**
 * Путь к пакету Playwright, если он лежит в npx-кеше. `null` — не нашли: значит попробуем
 * обычный `import('playwright')` (так пакет стоит в CI, где npx-кеша нет вовсе).
 */
export function findPlaywright() {
  if (process.env.PLAYWRIGHT_PKG) return process.env.PLAYWRIGHT_PKG;
  const npx = path.join(homedir(), 'AppData/Local/npm-cache/_npx');
  for (const hash of existsSync(npx) ? readdirSync(npx) : []) {
    const p = path.join(npx, hash, 'node_modules/playwright/index.js');
    if (existsSync(p)) return p;
  }
  return null;
}

/**
 * Путь к исполняемому файлу браузера, если он найден в локальной установке. `null` — не нашли:
 * тогда браузер выбирает сам Playwright (в CI он ставит его себе `playwright install chromium`).
 * Ищем ТОЛЬКО windows-сборку headless shell: это путь для машины разработчика, где Playwright
 * приехал через npx и обычно ждёт версию браузера свежее установленной.
 */
export function findChromium() {
  if (process.env.CHROMIUM_EXE) return process.env.CHROMIUM_EXE;
  const root = path.join(homedir(), 'AppData/Local/ms-playwright');
  const builds = (existsSync(root) ? readdirSync(root) : [])
    .filter(d => d.startsWith('chromium_headless_shell-'))
    .sort((a, b) => Number(b.split('-')[1]) - Number(a.split('-')[1]));
  for (const b of builds) {
    const exe = path.join(root, b, 'chrome-headless-shell-win64/chrome-headless-shell.exe');
    if (existsSync(exe)) return exe;
  }
  return null;
}

/**
 * Запускает браузер. Два разных окружения:
 *  - машина разработчика: Playwright из npx-кеша + УЖЕ установленная сборка браузера;
 *  - CI (linux): пакет в node_modules, браузер ставит сам Playwright — путь не подставляем.
 * Оба находятся сами, оба переопределяются `PLAYWRIGHT_PKG` / `CHROMIUM_EXE`.
 */
export async function launchBrowser() {
  const pkg = findPlaywright();
  const pw = await import(pkg ? pathToFileURL(pkg).href : 'playwright');
  const { chromium } = pw.default ?? pw;   // пакет CJS — интероп кладёт экспорт в default
  const executablePath = findChromium();
  return chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
}

/**
 * Вход по форме. Ждём именно появления токена: без этого следующий `goto` успевает уйти
 * раньше сохранения, и ProtectedRoute вернёт на /login. Смотрим оба хранилища — «Запомнить
 * меня» выбирает между localStorage и sessionStorage (см. shared/api/token.ts), и прогон
 * не должен зависеть от того, каким это поле стоит по умолчанию.
 *
 * ⚠️ Отказ сервера называется ОТКАЗОМ, с кодом. Входы ограничены по частоте — 30 за пять минут с
 * одного адреса (политика `login` на сервере), — а все наборы ходят с одного. Пока прогон целиком
 * шёл шесть минут, входы делились между двумя окнами; без пауз (issue #1160) все они попадают в
 * одно. Сейчас их восемнадцать, запас есть, но следующий набор с тремя-четырьмя входами его
 * съест — и без этих слов упёрся бы в «истёк срок ожидания токена», по которому причину не найти.
 */
export async function login(page, email = EMAIL, password = PASSWORD) {
  await page.goto(`${BASE}/login`);
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  const answered = page.waitForResponse(
    r => r.request().method() === 'POST' && new URL(r.url()).pathname.endsWith('/auth/login'),
    { timeout: 15_000 });
  await page.click('button[type=submit]');
  const answer = await answered;
  if (!answer.ok()) {
    throw new Error(`вход под ${email} отклонён: ${answer.status()}` + (answer.status() === 429
      ? ' — исчерпан предел частоты входов (30 за 5 минут с одного адреса)' : ''));
  }
  // Срок — третьим аргументом: второй у `waitForFunction` это аргумент функции, и стоявшие там
  // «10 секунд» молча не действовали — ждали умолчательные тридцать.
  await page.waitForFunction(
    () => !!(localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token')),
    null, { timeout: 10_000 });
}

const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));

/** Запросы страницы, которые ещё в полёте, и счётчик начатых — на них стоит `settled()`. */
const watched = new WeakMap();

/**
 * Ставит страницу под наблюдение за запросами. Вызывается СРАЗУ после `newPage()` — до первого
 * перехода: запрос, ушедший раньше подписки, для `settled()` не существует, и она объявила бы
 * страницу затихшей при работающей загрузке.
 */
export function watchRequests(page) {
  if (watched.has(page)) return;
  const net = { inflight: new Set(), started: 0, loading: null };
  page.on('request', r => {
    if (r.isNavigationRequest() && r.frame() === page.mainFrame()) net.loading = r;
    net.inflight.add(r);
    net.started++;
  });
  // Загрузка нового документа обрывает запросы прежнего, и об оборванных так браузер НЕ сообщает
  // ни «завершён», ни «не удался» — они остались бы «в полёте» навсегда (проверено: первым же
  // прогоном `settled()` двадцать секунд ждала запрос страницы входа, уже заменённой переходом).
  //
  // Снимаем их в миг, когда новый документ ЗАНЯЛ страницу, а не когда ушёл запрос за ним: между
  // этими двумя событиями прежний документ ещё жив и успевает послать своё (перечитывание после
  // только что сохранённой темы — и следом `reload()`), так что снятое раньше набралось бы заново.
  //
  // Смена адреса внутри приложения сюда не попадает: запроса за документом у неё нет, `loading`
  // пуст — и запросы живого документа остаются на счету.
  page.on('framenavigated', frame => {
    if (frame !== page.mainFrame() || !net.loading) return;
    const document = net.loading;
    net.loading = null;
    const stillLoading = net.inflight.has(document);
    net.inflight.clear();
    if (stillLoading) net.inflight.add(document);
  });
  page.on('requestfinished', r => net.inflight.delete(r));
  page.on('requestfailed', r => {
    net.inflight.delete(r);
    if (net.loading === r) net.loading = null;   // загрузка не состоялась — документ прежний
  });
  watched.set(page, net);
}

/**
 * Ждёт, пока приложение ЗАКОНЧИТ отвечать на предыдущее действие: запросов в полёте нет, очередь
 * задач браузера пуста, кадр нарисован — и за это время не ушло ни одного нового запроса. Замена
 * фиксированным паузам (issue #1160): пауза платится целиком, даже когда экран готов через 50 мс,
 * и при этом ничего не обещает — на медленном раннере её может не хватить.
 *
 * <p>Два круга подряд, а не один: ответ → перерисовка → эффект → следующий запрос идут разными
 * задачами, и между ними браузер вправе нарисовать кадр. Один спокойный круг может попасть ровно в
 * этот зазор.</p>
 *
 * ⚠️ Чего она НЕ видит — того, что приложение отложило ТАЙМЕРОМ: поиск с задержкой ввода, откат
 * поля даты через 120 мс после потери фокуса, предпросмотр документа через полторы секунды. Там
 * ждут сам результат — `until()`.
 *
 * ⚠️ Истечение срока — отказ, а не «ну, наверное, готово»: продолжи проверка по незатихшей
 * странице, она покраснела бы шагом позже и уже про другое. В отказе названы запросы, которые
 * не дали дождаться.
 */
export async function settled(page, { timeout = 20_000 } = {}) {
  const net = watched.get(page);
  if (!net) throw new Error('settled(): страница не под наблюдением — watchRequests(page) зовут сразу после newPage()');
  const deadline = Date.now() + timeout;
  let calm = 0;
  while (calm < 2) {
    if (Date.now() > deadline) {
      const busy = [...net.inflight].map(r => `${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`);
      throw new Error(`страница не затихла за ${timeout / 1000} с: `
        + (busy.length ? `в полёте ${busy.join(', ')}` : 'запросы уходят один за другим'));
    }
    if (net.inflight.size > 0) { calm = 0; await sleep(15); continue; }
    const before = net.started;
    // Свободная очередь задач, затем кадр. Страховочный таймер — на случай, когда кадров нет вовсе
    // (страница в фоне): без него ожидание повисло бы до общего срока.
    const drained = await page.evaluate(() => new Promise(done => {
      const frame = () => requestAnimationFrame(() => done(true));
      setTimeout(() => done(true), 400);
      if ('requestIdleCallback' in window) requestIdleCallback(frame, { timeout: 300 }); else setTimeout(frame, 0);
    })).catch(() => false);   // переход посреди ожидания рвёт контекст — это «ещё не затихла»
    if (drained && net.inflight.size === 0 && net.started === before) calm++;
    else { calm = 0; if (!drained) await sleep(15); }
  }
}

/**
 * Повторяет `probe`, пока она не перестанет бросать, и возвращает её результат. Срок вышел —
 * наружу уходит ПОСЛЕДНИЙ отказ самой `probe`, со всеми её словами: сообщение проверки не
 * подменяется «истёк срок ожидания».
 *
 * ⚠️ Годится только для утверждения, которое ДО действия было ложным. Утверждению «не изменилось»
 * она вернёт успех в первый же миг — раньше, чем поломка успеет проявиться, — и проверка
 * перестанет что-либо стеречь. Такие ждут событие, после которого поломка возможна (`settled()`,
 * ответ на запрос), и читают значение один раз.
 */
export async function until(probe, { timeout = 10_000, every = 40 } = {}) {
  const deadline = Date.now() + timeout;
  for (;;) {
    try { return await probe(); }
    catch (e) { if (Date.now() >= deadline) throw e; }
    await sleep(every);
  }
}

/** Выход «изнутри»: чистим оба хранилища, иначе сессия может пережить сброс. */
export async function clearSession(page) {
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
}

/**
 * Счётчик проверок: `check` не роняет прогон на первом провале, `summarize` печатает итог.
 *
 * `skip` — для проверок, которым в этом окружении нечем работать (issue #872): разбиению PDF нужны
 * распознанные страницы, то есть ИИ-движок, а в CI его нет. Пропуск ЗАЯВЛЕН, а не сделан молча:
 * зелёный прогон обязан говорить, сколько проверок он не делал, — иначе «69 из 82» незаметно
 * превращается в «69 из 69», и недостающие тринадцать перестают существовать.
 */
export function createChecks() {
  const results = [];
  const skipped = [];
  async function check(name, fn) {
    try { await fn(); results.push([name, true, '']); console.log(`  ✓ ${name}`); }
    catch (e) { results.push([name, false, e.message]); console.log(`  ✗ ${name} — ${e.message}`); }
  }
  function skip(name, reason) {
    skipped.push([name, reason]);
    console.log(`  — ${name} — пропущено: ${reason}`);
  }
  function summarize(title) {
    const failed = results.filter(([, ok]) => !ok);
    console.log(`\n${results.length - failed.length}/${results.length} проверок прошло`
      + (skipped.length ? `, ${skipped.length} пропущено: ${skipped.map(([n]) => n).join(', ')}` : ''));
    if (failed.length) { console.error('ПРОВАЛ:', failed.map(([n]) => n).join(', ')); return 1; }
    console.log(`${title} — OK`);
    return 0;
  }
  return { check, skip, summarize };
}
