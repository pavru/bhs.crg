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
export const LOCALE = process.env.SMOKE_LOCALE || 'ru-RU';
export const EMAIL = process.env.SMOKE_EMAIL || 'admin@bhs.local';
export const PASSWORD = process.env.SMOKE_PASSWORD || 'Demo12345!';

/**
 * Путь к пакету Playwright: из PLAYWRIGHT_PKG (так его находит CI — пакет стоит в своём
 * каталоге, рядом с браузером, см. ci.yml) или из npx-кеша. `null` — не нашли: тогда обычный
 * `import('playwright')`.
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
 * тогда браузер выбирает сам Playwright (в CI он ставит себе headless shell — `playwright install
 * --only-shell chromium`; запуск ниже берёт именно его).
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
 *  - CI (linux): пакет по PLAYWRIGHT_PKG, браузер ставит сам Playwright — путь не подставляем.
 * Оба находятся сами, оба переопределяются `PLAYWRIGHT_PKG` / `CHROMIUM_EXE`.
 */
export async function launchBrowser() {
  const pkg = findPlaywright();
  const pw = await import(pkg ? pathToFileURL(pkg).href : 'playwright');
  const { chromium } = pw.default ?? pw;   // пакет CJS — интероп кладёт экспорт в default
  const executablePath = findChromium();
  const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });

  // Язык браузера назван явно (задача N2, issue #1103): формат чисел и дат у «системного» языка
  // следует браузеру, а проверки сверяют текст экрана. Без этого прогон зелёный на машине
  // разработчика и красный на раннере, у которого язык английский.
  //
  // ⚠️ Языком контекста, а не ключом запуска `--lang`: на Linux безголовый Chromium ключ не слушает
  // (проверено раннером — прогон остался английским), а язык контекста действует везде. Подставляется
  // здесь, чтобы ни одному прогону не пришлось о нём помнить; названный прогоном язык сильнее.
  //
  // Туда же — сценарий, называющий версию счёта в запросах самих прогонов (см. `nameSeenVersion`).
  for (const open of ['newContext', 'newPage']) {
    const original = browser[open].bind(browser);
    browser[open] = async (options = {}) => {
      const opened = await original({ locale: LOCALE, ...options });
      await opened.addInitScript(nameSeenVersion);
      return opened;
    };
  }
  return browser;
}

/**
 * Правка счёта обязана назвать его версию заголовком `If-Match` (issue #1176). Прогоны готовят данные
 * своими запросами (`fetch` из страницы) — и это «форма, которая видит свежее»: перед правкой счёт
 * читается, и его версия подставляется.
 *
 * ⚠️ Подменяется только `fetch`. Приложение ходит через axios (XMLHttpRequest), и его запросы сценарий
 * не трогает: версию, которую называет ФОРМА, прогон проверяет как есть.
 *
 * Выполняется в странице: снаружи сюда ничего не замкнуть.
 */
function nameSeenVersion() {
  const plain = window.fetch.bind(window);
  window.fetch = async (input, init = {}) => {
    const url = typeof input === 'string' ? input : input.url;
    const invoice = /\/api\/costs\/invoices\/([0-9a-f-]{36})(\/|$)/i.exec(url);
    const headers = new Headers(init.headers ?? {});
    if (!invoice || (init.method ?? 'GET').toUpperCase() === 'GET' || headers.has('If-Match')) return plain(input, init);

    const seen = await plain(`/api/costs/invoices/${invoice[1]}`, { headers: { Authorization: headers.get('Authorization') } });
    if (seen.ok) headers.set('If-Match', (await seen.json()).version);
    return plain(input, { ...init, headers });
  };
}

/**
 * Вход по форме: страница входа, отправка, токен. Заодно ставит страницу под наблюдение за
 * запросами (`watchRequests`) — вход у каждого набора первым делом, и отдельный вызов, о котором
 * надо помнить, забывали бы ровно там, где `settled()` понадобится позже.
 */
export async function login(page, email = EMAIL, password = PASSWORD) {
  await watchRequests(page);
  await page.goto(`${BASE}/login`);
  await submitLogin(page, email, password);
}

/**
 * Отправка формы входа на УЖЕ открытой странице входа — для проверок, которым важно войти тем же
 * деревом, что рисовало страницу входа, без перехода (`shared-ui`: смена человека в одной вкладке).
 *
 * Ждём именно появления токена: без этого следующий `goto` успевает уйти раньше сохранения, и
 * ProtectedRoute вернёт на /login. Смотрим оба хранилища — «Запомнить меня» выбирает между
 * localStorage и sessionStorage (см. shared/api/token.ts), и прогон не должен зависеть от того,
 * каким это поле стоит по умолчанию.
 *
 * ⚠️ Отказ сервера называется ОТКАЗОМ, с кодом. Входы ограничены по частоте — 30 за пять минут с
 * одного адреса (политика `login` на сервере), — а все наборы ходят с одного. Пока прогон целиком
 * шёл шесть минут, входы делились между двумя окнами; без пауз (issue #1160) все они попадают в
 * одно. Без этих слов набор упирался бы в «истёк срок ожидания токена», по которому причину не
 * найти. Сколько входов в прогоне CI и сколько ещё помещается — считает `suites.test.mjs`, а не
 * этот комментарий: число, записанное словами, устарело первым же слиянием.
 */
export async function submitLogin(page, email = EMAIL, password = PASSWORD) {
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  // Ожидание ответа и щелчок — ОДНИМ обещанием. Заведённое отдельно, ожидание осталось бы без
  // хозяина при отказе щелчка, и его собственный отказ через пятнадцать секунд ронял бы процесс —
  // посреди другой проверки и без итоговой сводки.
  const [answer] = await Promise.all([
    page.waitForResponse(
      r => r.request().method() === 'POST' && new URL(r.url()).pathname.endsWith('/auth/login'),
      { timeout: 15_000 }),
    page.click('button[type=submit]'),
  ]);
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

/** Запрос за документом главного кадра. `frame()` у запроса вправе бросить — тогда это не он. */
function isDocumentRequest(page, request) {
  try { return request.isNavigationRequest() && request.frame() === page.mainFrame(); }
  catch { return false; }
}

/**
 * Ставит страницу под наблюдение за запросами. `login()` делает это сам; звать отдельно нужно
 * только странице, которая обходится без входа, — и тогда ДО первого перехода: запрос, ушедший
 * раньше подписки, для `settled()` не существует, и она объявила бы страницу затихшей при
 * работающей загрузке.
 */
export function watchRequests(page) {
  const known = watched.get(page);
  if (known) return known.ready;
  const net = { inflight: new Set(), started: 0, document: null, ready: null };
  watched.set(page, net);
  page.on('request', r => {
    if (isDocumentRequest(page, r)) net.document = r;
    net.inflight.add(r);
    net.started++;
  });
  page.on('requestfinished', r => net.inflight.delete(r));
  page.on('requestfailed', r => net.inflight.delete(r));
  // Загрузка нового документа обрывает запросы прежнего, и об оборванных так браузер НЕ сообщает
  // ни «завершён», ни «не удался» — они остались бы «в полёте» навсегда (проверено: первым же
  // прогоном `settled()` двадцать секунд ждала запрос страницы входа, уже заменённой переходом).
  //
  // Снимаем их в миг, когда новый документ ЗАНЯЛ страницу, а не когда ушёл запрос за ним: между
  // этими двумя событиями прежний документ ещё жив и успевает послать своё (перечитывание после
  // только что сохранённой темы — и следом `reload()`), так что снятое раньше набралось бы заново.
  //
  // ⚠️ Этот миг берём у самого браузера, а не у `framenavigated`: то событие приходит и на смену
  // адреса ВНУТРИ приложения, и отличить одно от другого по нему нельзя. Пока различали по «был ли
  // запрос за документом», смена адреса, случившаяся при уже ушедшем запросе, принималась за
  // приход документа: счёт снимался раньше времени, а настоящий приход уже не снимал ничего — и
  // оборванные запросы прежнего экрана висели до конца прогона (ревью PR #1161; так бывает сразу
  // после входа, когда проверка зовёт `goto`, а приложение в тот же миг уходит на стартовый экран).
  // У браузера это два РАЗНЫХ события: `Page.frameNavigated` — только про новый документ.
  net.ready = (async () => {
    const session = await page.context().newCDPSession(page);
    session.on('Page.frameNavigated', ({ frame }) => {
      if (frame.parentId) return;   // вложенный кадр: об оборванном в нём браузер сообщает сам
      const loading = net.document && net.inflight.has(net.document) ? net.document : null;
      net.inflight.clear();
      if (loading) net.inflight.add(loading);   // сам документ ещё докачивается
    });
    await session.send('Page.enable');
  })();
  return net.ready;
}

const BUSY = Symbol('страница не ответила');

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
 * не дали дождаться. Срок действует и тогда, когда страница не отвечает вовсе: вопрос «свободна
 * ли очередь» задаётся ей самой, и занятый поток держал бы ожидание столько, сколько занят.
 *
 * Поведение проверяется `harness-smoke.mjs` — на цепочках, а не на приложении.
 */
export async function settled(page, { timeout = 20_000 } = {}) {
  const net = watched.get(page);
  if (!net) throw new Error('settled(): страница не под наблюдением — её ставит login(), а без входа — watchRequests(page) до первого перехода');
  await net.ready;
  const deadline = Date.now() + timeout;
  const flying = () => [...net.inflight].map(r => `${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`);
  let calm = 0;
  while (calm < 2) {
    if (Date.now() > deadline) {
      const busy = flying();
      throw new Error(`страница не затихла за ${timeout / 1000} с: `
        + (busy.length ? `в полёте ${busy.join(', ')}` : 'запросы уходят один за другим'));
    }
    if (net.inflight.size > 0) { calm = 0; await sleep(15); continue; }
    const before = net.started;
    // Свободная очередь задач, затем кадр. Страховочный таймер — на случай, когда кадров нет вовсе
    // (страница в фоне): без него ожидание повисло бы до общего срока.
    const asked = page.evaluate(() => new Promise(done => {
      const frame = () => requestAnimationFrame(() => done(true));
      setTimeout(() => done(true), 400);
      if ('requestIdleCallback' in window) requestIdleCallback(frame, { timeout: 300 }); else setTimeout(frame, 0);
    })).catch(() => false);   // переход посреди ожидания рвёт контекст — это «ещё не затихла»
    // Таймер срока снимаем сами: оставленный, он держал бы процесс набора ещё двадцать секунд
    // после последней проверки.
    let timer;
    const late = new Promise(resolve => { timer = setTimeout(() => resolve(BUSY), deadline - Date.now()); });
    const drained = await Promise.race([asked, late]).finally(() => clearTimeout(timer));
    if (drained === BUSY) {
      const busy = flying();
      throw new Error(`страница не отвечает ${timeout / 1000} с — занята так, что не может сказать, затихла ли`
        + (busy.length ? `; в полёте ${busy.join(', ')}` : ''));
    }
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

/**
 * Запрос к приложению ТЕМ ЖЕ токеном, что у открытой страницы. Отказ не бросается — возвращается
 * кодом: прогону под ролью бывает нужен именно он. Общий помощник заведён с `costs-smoke` (issue
 * #1102); в старших наборах ещё живут свои копии — переносить их сюда по мере правок.
 */
export function callApi(page, method, path, body) {
  return page.evaluate(async ([method, path, body]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const res = await fetch(`/api${path}`, {
      method,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: body === null ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    return { status: res.status, body: text && res.ok ? JSON.parse(text) : text };
  }, [method, path, body ?? null]);
}

/** Адрес «Реестра счетов» с готовым отбором — тем же видом, каким его собирает клиент. */
export const registryAddress = filter =>
  `${BASE}/tables/costs.invoices/registry#filter=${encodeURIComponent(JSON.stringify(filter))}`;

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
