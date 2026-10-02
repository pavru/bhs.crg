// Живая проверка САМОЙ ОБВЯЗКИ: `settled()`, `until()`, наблюдение за запросами, вход (issue #1160).
//
// Зачем. На `settled()` стоит полторы сотни ожиданий во всех наборах, и ошибается она в ТИХУЮ
// сторону: вернись она раньше времени — проверки вида «не изменилось» прочтут значение до поломки
// и останутся зелёными. Ни один набор этого не заметит: все они проверяют приложение, а не часы,
// по которым его проверяют. Здесь — те самые цепочки, на которых обвязку отлаживали руками, только
// записанные и гоняемые каждым прогоном.
//
// Приложение этому набору НЕ НУЖНО: он поднимает собственный крошечный сервер и ходит только к
// нему. Потому же он годится для машины, где нет ни базы, ни стенда, — нужен один браузер.
//
// Запуск (Git Bash):  node e2e/harness-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import http from 'node:http';

const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));

// ── Сервер-подделка ────────────────────────────────────────────────────────────
const BLANK = '<!doctype html><meta charset="utf-8"><title>пусто</title><body></body>';
const LOGIN = (withButton) => `<!doctype html><meta charset="utf-8"><title>вход</title>
<form>
  <input type="email"><input type="password">
  ${withButton ? '<button type="submit">Войти</button>' : ''}
</form>
<script>
  document.querySelector('form').addEventListener('submit', async e => {
    e.preventDefault();
    const r = await fetch('/api/auth/login', { method: 'POST' });
    if (r.ok) localStorage.setItem('access_token', 'подделка');
  });
</script>`;

/** Чем отвечает подделка входа; меняют сами проверки. */
const fake = { loginStatus: 200, loginButton: true };
/** Какие «вечные» запросы дошли до сервера — проверке нужно знать, что запрос УЖЕ в полёте. */
const hanging = new Set();

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  const html = (body) => { res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }); res.end(body); };
  const delay = Number(url.searchParams.get('ms') ?? 0);
  if (delay) await sleep(delay);
  switch (url.pathname) {
    case '/hang': hanging.add(url.searchParams.get('who')); return;   // не отвечаем никогда
    case '/slow': res.writeHead(200, { 'content-type': 'text/plain' }); res.end('ok'); return;
    case '/login': html(LOGIN(fake.loginButton)); return;
    case '/api/auth/login': res.writeHead(fake.loginStatus); res.end(); return;
    case '/frame': html(`${BLANK}<script>fetch('/hang?who=frame')</script>`); return;
    default: html(BLANK);
  }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const ORIGIN = `http://127.0.0.1:${server.address().port}`;

// Обвязка читает адрес приложения из окружения В МИГ ЗАГРУЗКИ — поэтому подделку называем раньше.
process.env.SMOKE_BASE = ORIGIN;
const { launchBrowser, login, submitLogin, createChecks, watchRequests, settled, until }
  = await import('./harness.mjs');

const unhandled = [];
process.on('unhandledRejection', e => unhandled.push(e));

const browser = await launchBrowser();
const page = await browser.newPage();
await watchRequests(page);
const { check, summarize } = createChecks();

/** Отказ с ожидаемыми словами; успех или отказ про другое — провал проверки. */
const refused = async (promise, words) => {
  const said = await promise.then(() => null, e => e.message);
  if (said === null) throw new Error('отказа нет — вызов завершился успехом');
  if (!words.test(said)) throw new Error(`отказ не про то: «${said.slice(0, 200)}»`);
};
const hangs = (who) => until(async () => { if (!hanging.has(who)) throw new Error(`запрос «${who}» до сервера не дошёл`); });

try {

// ── settled(): чего она обязана дождаться ──────────────────────────────────────
await check('settled-is-cheap-on-a-calm-page', async () => {
  await page.goto(`${ORIGIN}/blank`);
  const started = Date.now();
  await settled(page);
  const took = Date.now() - started;
  if (took > 1500) throw new Error(`на спокойной странице ожидание заняло ${took} мс`);
});

await check('settled-waits-for-a-request-in-flight', async () => {
  await page.evaluate(() => { window.got = false; fetch('/slow?ms=500').then(() => { window.got = true; }); });
  await settled(page);
  if (!(await page.evaluate(() => window.got))) throw new Error('страница объявлена затихшей до ответа на запрос');
});

// Запросы гуськом, как их шлёт приложение: ответ → перерисовка (кадр) → эффект (следующая задача) →
// запрос. Тела ответов ЧИТАЮТСЯ, как это делает приложение: без чтения Playwright сообщает о
// завершении запроса на десятки миллисекунд позже, и просветов между запросами не остаётся вовсе.
await check('settled-waits-for-requests-chained-through-frames-and-tasks', async () => {
  await page.evaluate(() => {
    window.done = false;
    const link = () => fetch('/slow?ms=30').then(r => r.text())
      .then(() => new Promise(next => requestAnimationFrame(next)))
      .then(() => new Promise(next => setTimeout(next, 0)));
    let chain = Promise.resolve();
    for (let i = 0; i < 6; i++) chain = chain.then(link);
    chain.then(() => fetch('/slow?ms=30')).then(r => r.text()).then(() => { window.done = true; });
  });
  await settled(page);
  if (!(await page.evaluate(() => window.done))) throw new Error('цепочка запросов оборвана посередине');
});

// ── settled(): счёт кругов — на подставной странице ────────────────────────────
//
// «Два спокойных круга» и «запрос, ушедший и вернувшийся внутри круга» в браузере проверяются
// только вероятностно: попадёт ли запрос в просвет, решают доли кадра, и проверка на настоящей
// странице выходила либо слепой к поломке, либо красной без поломки (пробовали оба вида). Поэтому
// здесь страница подставная: события запросов и ответ на «свободна ли очередь» подаёт сама
// проверка, и круги считаются точно.
const fakePage = (onRound = () => true) => {
  const handlers = {};
  const fake = {
    rounds: 0,
    on: (event, handler) => { (handlers[event] ??= []).push(handler); },
    emit: (event, arg) => { for (const handler of handlers[event] ?? []) handler(arg); },
    context: () => ({ newCDPSession: async () => ({ on() {}, send: async () => {} }) }),
    mainFrame: () => fake,
    evaluate: async () => onRound(++fake.rounds, fake),
  };
  return fake;
};
const fakeRequest = (url) => ({ isNavigationRequest: () => false, frame: () => null, method: () => 'GET', url: () => url });
const roundsTaken = async (onRound) => {
  const fake = fakePage(onRound);
  await watchRequests(fake);
  await settled(fake, { timeout: 5000 });
  return fake.rounds;
};

await check('settled-takes-two-calm-rounds-not-one', async () => {
  const rounds = await roundsTaken();
  if (rounds !== 2) throw new Error(`на спокойной странице кругов ${rounds}, а не два`);
});

// Запрос успел и уйти, и вернуться внутри первого круга: «в полёте» пусто и до круга, и после.
// Выдаёт его только счётчик начатых — и счёт спокойных кругов обязан начаться заново.
await check('settled-starts-over-after-a-request-that-came-and-went-within-a-round', async () => {
  const rounds = await roundsTaken((round, fake) => {
    if (round === 1) {
      const request = fakeRequest('http://x/api/quick');
      fake.emit('request', request);
      fake.emit('requestfinished', request);
    }
    return true;
  });
  if (rounds !== 3) throw new Error(`кругов ${rounds}: запрос внутри круга счёта не сбросил (ждали три)`);
});

await check('settled-starts-over-after-a-request-it-had-to-wait-for', async () => {
  const rounds = await roundsTaken((round, fake) => {
    if (round === 1) {
      const request = fakeRequest('http://x/api/slow');
      fake.emit('request', request);
      setTimeout(() => fake.emit('requestfinished', request), 80);
    }
    return true;
  });
  if (rounds !== 3) throw new Error(`кругов ${rounds}: после дождавшегося запроса счёт не начат заново (ждали три)`);
});

// Переход посреди вопроса рвёт его — это «ещё не затихла», а не «затихла» и не отказ.
await check('settled-starts-over-when-the-round-itself-was-torn', async () => {
  const rounds = await roundsTaken((round) => {
    if (round === 2) throw new Error('Execution context was destroyed');
    return true;
  });
  if (rounds !== 4) throw new Error(`кругов ${rounds}: оборванный круг зачтён за спокойный (ждали четыре)`);
});

await check('settled-refuses-by-the-deadline-and-names-the-request', async () => {
  await page.evaluate(() => { fetch('/hang?who=deadline'); });
  await refused(settled(page, { timeout: 700 }), /не затихла за 0\.7 с: в полёте GET \/hang\?who=deadline/);
});

// ── Новый документ: оборванные запросы прежнего снимаются ──────────────────────
await check('new-document-drops-the-requests-it-aborted', async () => {
  // Запрос «deadline» из проверки выше всё ещё висит — переход обязан его снять.
  await page.goto(`${ORIGIN}/blank`);
  await settled(page, { timeout: 3000 });
});

// Обратная сторона: смена адреса ВНУТРИ приложения документа не меняет, и его запросы обязаны
// остаться на счету — иначе страница объявлялась бы затихшей при живом запросе.
await check('address-change-within-the-document-keeps-its-requests', async () => {
  await page.evaluate(() => {
    window.got = false;
    fetch('/slow?ms=600').then(() => { window.got = true; });
    history.pushState({}, '', '/elsewhere');
  });
  await settled(page);
  if (!(await page.evaluate(() => window.got))) throw new Error('смена адреса сняла со счёта живой запрос');
});

// Гонка из ревью PR #1161: запрос за новым документом уже ушёл, а прежний ещё жив — меняет адрес и
// шлёт своё. Так бывает сразу после входа: проверка зовёт `goto`, приложение в тот же миг переходит
// на стартовый экран. Запросы прежнего документа обязаны уйти со счёта, когда придёт новый.
await check('address-change-while-a-document-is-loading-does-not-leak-requests', async () => {
  await page.goto(`${ORIGIN}/blank`);
  await page.evaluate(() => { fetch('/hang?who=before-push'); });
  await hangs('before-push');
  // Смену адреса заводим ТАЙМЕРОМ в самой странице, до перехода: вопрос, заданный странице извне,
  // пока документ грузится, ждёт его прихода — и до прежнего документа не доходит вовсе.
  await page.evaluate(() => {
    setTimeout(() => { history.pushState({}, '', '/pushed'); fetch('/hang?who=after-push'); }, 200);
  });
  await page.goto(`${ORIGIN}/doc?ms=700`);
  if (!hanging.has('after-push')) throw new Error('прежний документ не успел сменить адрес — гонки не было');
  await settled(page, { timeout: 3000 });
});

await check('removed-iframe-drops-its-requests', async () => {
  await page.goto(`${ORIGIN}/blank`);
  await page.evaluate(() => {
    const frame = document.createElement('iframe');
    frame.src = '/frame';
    document.body.append(frame);
  });
  await hangs('frame');
  await page.evaluate(() => document.querySelector('iframe').remove());
  await settled(page, { timeout: 3000 });
});

await check('settled-refuses-a-page-nobody-watches', async () => {
  const stray = await browser.newPage();
  try { await refused(settled(stray), /не под наблюдением/); }
  finally { await stray.close(); }
});

// Срок обязан действовать и тогда, когда страница не отвечает вовсе: занятый поток не пускает
// даже вопрос «свободна ли очередь», и без своего срока ожидание висело бы столько же, сколько он.
await check('settled-deadline-holds-while-the-page-is-busy', async () => {
  await page.goto(`${ORIGIN}/blank`);
  await page.evaluate(() => { setTimeout(() => { const from = Date.now(); while (Date.now() - from < 3000); }, 30); });
  await sleep(150);
  const started = Date.now();
  await refused(settled(page, { timeout: 800 }), /не отвечает/);
  const took = Date.now() - started;
  await sleep(3000 - Math.min(took, 3000));   // даём странице освободиться — следующим проверкам она нужна живой
  if (took > 2000) throw new Error(`срок 0,8 с, а отказ пришёл через ${took} мс`);
});

// ── until() ────────────────────────────────────────────────────────────────────
await check('until-returns-what-the-probe-returned-once-it-stops-throwing', async () => {
  let calls = 0;
  const got = await until(async () => { if (++calls < 3) throw new Error('рано'); return `с ${calls}-го раза`; });
  if (got !== 'с 3-го раза') throw new Error(`вернулось «${got}»`);
});

await check('until-gives-up-with-the-last-words-of-the-probe', async () => {
  let calls = 0;
  const started = Date.now();
  await refused(until(async () => { throw new Error(`своё слово ${++calls}`); }, { timeout: 300 }), /^своё слово \d+$/);
  const took = Date.now() - started;
  if (took < 300 || took > 1500) throw new Error(`срок 0,3 с, а отказ пришёл через ${took} мс`);
  if (calls < 2) throw new Error('утверждение проверено один раз — повторов не было');
});

// ── Вход ───────────────────────────────────────────────────────────────────────
// Своя страница, и нарочно БЕЗ watchRequests: наблюдение обязан поставить сам вход — иначе набор,
// забывший о нём, узнавал бы об этом отказом `settled()` посреди прогона.
const guest = await browser.newPage();
guest.setDefaultTimeout(1500);

await check('login-puts-the-page-under-watch', async () => {
  fake.loginStatus = 200;
  await login(guest);
  await guest.evaluate(() => { window.got = false; fetch('/slow?ms=400').then(() => { window.got = true; }); });
  await settled(guest);
  if (!(await guest.evaluate(() => window.got))) throw new Error('после входа страница не под наблюдением');
});

await check('login-names-the-rate-limit', async () => {
  fake.loginStatus = 429;
  await refused(login(guest), /отклонён: 429 — исчерпан предел частоты входов/);
});

// Вход БЕЗ перехода — тем же деревом, что рисовало страницу входа (так входят проверки настроек).
// Отказ и тут обязан называться отказом, а не «истёк срок ожидания токена».
await check('submit-login-names-a-refusal-too', async () => {
  fake.loginStatus = 401;
  await guest.goto(`${ORIGIN}/login`);
  await refused(submitLogin(guest), /отклонён: 401$/);
});

// Кнопки нет — щелчок отказывает. Ожидание ответа на вход при этом никто уже не ждёт, и отказ
// ЕГО срока, брошенный мимо всех, ронял бы процесс позже — посреди чужой проверки и без итога.
// Закрытие страницы отклоняет такое ожидание сразу, так что пятнадцать секунд ждать незачем.
await check('failed-login-leaves-no-stray-rejection', async () => {
  fake.loginStatus = 200;
  fake.loginButton = false;
  await refused(login(guest), /button\[type=submit\]/);
  await guest.close();
  await sleep(100);
  if (unhandled.length) throw new Error(`отказ, брошенный мимо вызывающего: ${unhandled[0]?.message?.slice(0, 160)}`);
});

} finally {
  await browser.close();
  server.closeAllConnections();
  server.close();
}

process.exitCode = summarize('Обвязка живых прогонов');
