// Живой прогон ЭКРАНА ТАБЛИЦЫ модуля (задача G1e этапа 2, issue #1092, ТЗ CORE-33).
//
// ЗАЧЕМ ОН ЕСТЬ. Правила экрана — адрес ↔ состояние, слова итога, счёт скрытых колонок — проверены
// юнит-тестами без экрана. Пять обещаний задачи — про экран целиком, и у каждого названо, чем оно
// ломается (проверено поломкой, см. описание PR #1092):
//
//   1. table-states-are-five-and-different — одна таблица в пяти состояниях даёт ПЯТЬ текстов,
//      попарно неравных. Сверка каждого с ожидаемой строкой прошла бы и на экране, где четыре из
//      пяти показывают одно и то же. Ломается: сделать «поля нет в типе» той же пустотой, что
//      «отбор ничего не нашёл».
//   2. hidden-column-names-the-right — без права на суммы строка над таблицей называет число
//      колонок и КОД права; у администратора строки нет вовсе. Ломается: «3 колонки скрыты» без
//      кода; строка у администратора.
//   3. view-state-survives-reload-and-back — отбор, сортировка и состав колонок переживают F5;
//      «назад» возвращает ПРЕДЫДУЩИЙ ОТБОР, а не предыдущий щелчок. Ломается: писать в историю
//      каждое изменение — краснеет на числе шагов назад. И два действия быстрее перерисовки
//      складываются: считать изменение от нарисованного, а не от адреса, — второе сотрёт первое.
//   4. total-counts-the-selection-not-the-page — итог при отборе БОЛЬШЕ страницы равен сумме по
//      всему отбору. Ломается: считать итог по строкам страницы.
//   5. broken-filter-refuses-visibly — негодный отбор даёт видимый отказ с названием колонки, а не
//      «отбор ничего не нашёл». Ломается: съесть отказ и показать пустую выдачу.
//
// И готовое представление «Реестр счетов» (задача G4, issue #1097, ТЗ COST-20.1):
//
//   6. registry-is-a-named-setup — пункт навигации открывает ту же таблицу под настройкой модуля:
//      колонки в порядке таблицы заказчика, итоги под обеими суммами, пять мест под отбор — и
//      пустой адрес. Ломается: открыть таблицу всеми колонками под названием реестра.
//   7. registry-address-holds-only-the-difference — правка представления ложится в адрес одним
//      отличием и переживает перезагрузку. Ломается: писать в адрес настройку целиком либо читать
//      адрес от умолчаний таблицы — убранная колонка вернётся, а за ней придут все остальные.
//   8. registry-total-names-its-axis — под отбором периода сумма и её ИТОГ называют ось: «по дате
//      счёта, не по оплате». Ломается: оставить подпись только в шапке либо не дать вовсе.
//   9. unknown-view-is-refused — представления с таким кодом нет: экран это говорит, а не открывает
//      таблицу целиком под видом того, о чём просили.
//  10. registry-leads-to-the-whole-table — из реестра к таблице целиком ведёт ссылка, и отбор едет с
//      ней. Ломается: убрать ссылку (другого входа в таблицу в интерфейсе нет) либо вести без отбора.
//
// ⚠️ ДАННЫЕ — ТЕ ЖЕ, ЧТО СЕЕТ ПОСЕВ (e2e/seed-invoices.mjs, `TABLE_SEED`): счетов для итога нарочно
// больше страницы, и человек «модуль есть, счетов нет» заведён там же. Счета прогон досеивает САМ, той
// же функцией: в CI посев идёт до перезапуска приложения, когда типа счёта ещё нет, и счетов не сеет.
// Человека прогон не заводит — нет его, значит посева не было, и прогон отказывает вслух.
//
// ⚠️ ДВА СОСТОЯНИЯ ИЗ ПЯТИ ВЗЯТЬ СО СТЕНДА НЕГДЕ: «строк нет» (счета на стенде есть) и «модуль
// выключен» (он включён — иначе нечем проверять остальное). Для них ответ сервера ПЕРЕДЕЛЫВАЕТСЯ на
// лету из настоящего: строки убраны, либо колонкам поставлена причина «модуль выключен» — в том
// виде, какой сервер отдаёт сам (ModuleTableOffTests). Проверяется здесь экран, а не сервер.
//
// ⚠️ Элементы ищутся ПО ВИДИМОМУ ТЕКСТУ и ролям, без служебных атрибутов.
//
// Требует поднятых фронта и бэка, посеянных данных и ВКЛЮЧЁННОГО модуля `costs`.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/table-view-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, PASSWORD, launchBrowser, login, createChecks, settled, until } from './harness.mjs';
import { TABLE_SEED, ensureTableRows, tableSeedSum } from './seed-invoices.mjs';

const TABLE = `${BASE}/tables/costs.invoices`;
const USER_PASSWORD = process.env.SMOKE_USER_PASSWORD || PASSWORD;

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();

/** Фрагмент адреса из состояния — тем же письмом, что пишет экран. */
const hash = state => '#' + Object.entries(state)
  .map(([k, v]) => `${k}=${encodeURIComponent(typeof v === 'string' ? v : JSON.stringify(v))}`).join('&');
const condition = (column, op, value) => ({ type: 'condition', column, op, value });
const seeded = condition('Назначение', 'eq', TABLE_SEED.purpose);

/** Строки таблицы с данными — без строки состояния. */
const dataRows = p => p.locator('tbody tr').filter({ hasNot: p.locator('td[colspan]') });
/** Строка состояния — «почему строк нет». */
const stateCell = p => p.locator('tbody td[colspan]');
const headers = p => p.locator('thead th');
const chips = p => p.getByRole('group', { name: 'Условия отбора' });
const flat = text => text.replace(/[  ]/g, ' ').replace(/\s+/g, ' ').trim();

/** Открыть таблицу в состоянии и дождаться ответа. Перезагрузка — полная: адрес обязан хватить сам. */
async function open(p, state = {}) {
  // Сменился только фрагмент — браузер страницу не перезагружает: уходим с неё, чтобы зайти заново.
  await p.goto('about:blank');
  await p.goto(TABLE + (Object.keys(state).length ? hash(state) : ''), { waitUntil: 'networkidle' });
  await p.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10000 });
}

async function rowsBecome(p, count, what) {
  try {
    await p.waitForFunction(
      n => [...document.querySelectorAll('tbody tr')].filter(r => !r.querySelector('td[colspan]')).length === n,
      count, { timeout: 8000 });
  } catch {
    throw new Error(`${what}: строк ${await dataRows(p).count()}, а обязано быть ${count}`);
  }
}

/**
 * Переделать ответы сервера о таблице на время `run`. Ответ берётся НАСТОЯЩИЙ и правится — так
 * подделка не расходится с формой, которую сервер отдаёт на самом деле.
 */
async function withAnswers(p, change, run) {
  const pattern = '**/api/tables/costs.invoices**';
  await p.route(pattern, async route => {
    const response = await route.fetch();
    if (!response.ok()) return route.fulfill({ response });
    await route.fulfill({ response, json: change(await response.json()) });
  });
  try { await run(); } finally { await p.unroute(pattern); }
}

await login(page);

/** Запрос к приложению тем же токеном, что у открытой страницы. */
async function api(p, method, path, body) {
  return p.evaluate(async ([method, path, body]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const res = await fetch(`/api${path}`, {
      method,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: body === null ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text.slice(0, 200)}`);
    return text ? JSON.parse(text) : null;
  }, [method, path, body ?? null]);
}

// Данные на месте? Без них проверять нечего, и зелёный прогон был бы отчётом о работе, которой не было.
try {
  await ensureTableRows((method, path, body) => api(page, method, path, body));
  const filter = encodeURIComponent(JSON.stringify(seeded));
  const table = await api(page, 'GET', `/tables/costs.invoices?limit=1&filter=${filter}`);
  if (table.count !== TABLE_SEED.count)
    throw new Error(`счетов «${TABLE_SEED.purpose}» в таблице ${table.count}, а обязано быть ${TABLE_SEED.count}`);
} catch (e) {
  console.error(`Данных прогона нет: ${e.message}. Включён ли модуль costs у сервера `
    + '(Modules__Enabled=id,costs) и заведён ли тип счёта? Посев: npm run test:e2e:seed.');
  await browser.close();
  process.exit(1);
}

/** Пять состояний — текстами, как их видит человек. Сравниваются между собой в конце. */
const states = {};

try {
  // ── 1. Пять состояний ─────────────────────────────────────────────────────────────────────────

  await check('состояние «отбор ничего не нашёл» названо отбором, и чипы остаются', async () => {
    await open(page, { filter: condition('Номер', 'eq', 'такого-номера-нет-ни-у-одного-счёта') });
    await rowsBecome(page, 0, 'под отбором по несуществующему номеру');
    states.filteredOut = flat(await stateCell(page).innerText());
    if ((await chips(page).getByRole('button', { name: /^Снять условие/ }).count()) !== 1)
      throw new Error('при пустой выдаче чип пропал — объяснить пустоту стало нечем');
  });

  await check('состояние «строк нет» — без отбора и без строк', async () => {
    await withAnswers(page, t => (t.rows ? { ...t, rows: [], keys: [], count: 0 } : t), async () => {
      await open(page);
      await rowsBecome(page, 0, 'таблица без строк');
      states.noData = flat(await stateCell(page).innerText());
    });
  });

  await check('состояние «поля нет в типе»: колонка осталась, помечена, и её можно убрать', async () => {
    await open(page, { columns: 'Номер,ПолеКоторогоНет', filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета');

    const removed = headers(page).filter({ hasText: 'ПолеКоторогоНет' });
    await removed.waitFor();
    states.removed = flat(await dataRows(page).first().locator('td').nth(1).innerText());

    await removed.getByRole('button', { name: 'Убрать колонку «ПолеКоторогоНет»' }).click();
    await page.waitForFunction(() => document.querySelectorAll('thead th').length === 1, null, { timeout: 5000 })
      .catch(() => { throw new Error('колонка после «Убрать» осталась в таблице'); });
    if (decodeURIComponent(page.url()).includes('ПолеКоторогоНет'))
      throw new Error('колонка убрана с экрана, а в адресе осталась — вернётся после перезагрузки');
  });

  await check('состояние «модуль выключен» — словами, а не пустой таблицей', async () => {
    const reason = 'модуль «Счета и накладные» выключен';
    const turnOff = t => ({
      ...t, state: 'module-off',
      columns: t.columns.map(c => ({ ...c, unavailable: 'module-off', reason })),
      ...(t.rows ? { rows: [], keys: null, count: 0, totals: {} } : {}),
      // У выключенного модуля представлений нет вовсе (ModuleTableOffTests).
      ...(t.views ? { views: [] } : {}),
    });
    await withAnswers(page, turnOff, async () => {
      await open(page);
      await stateCell(page).waitFor({ timeout: 8000 });
      states.moduleOff = flat(await stateCell(page).innerText());

      // Тот же модуль под адресом с кодом представления: говорит таблица — теми же словами. Не
      // «представления нет» (его нет, потому что модуль выключен) и не вечное «Строки загружаются…».
      await page.goto('about:blank');
      await page.goto(`${TABLE}/registry`, { waitUntil: 'networkidle' });
      await stateCell(page).waitFor({ timeout: 8000 })
        .catch(() => { throw new Error('под кодом представления выключенный модуль состояния не назвал — экран ждёт строк, которых не запрашивал'); });
      const under = flat(await stateCell(page).innerText());
      if (under !== states.moduleOff) throw new Error(`под кодом представления состояние другое: «${under}»`);
    });
  });

  // ── 2. Колонка, закрытая правом ───────────────────────────────────────────────────────────────

  const narrow = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
  narrow.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

  await check('hidden-column-names-the-right: без права на суммы названы число колонок и код права', async () => {
    await login(narrow, TABLE_SEED.narrowEmail, USER_PASSWORD);
    await open(narrow, { filter: seeded });
    await rowsBecome(narrow, TABLE_SEED.count, 'посеянные счета у человека без права на суммы');

    const closed = (await api(narrow, 'GET', '/tables/costs.invoices/columns')).columns
      .filter(c => c.unavailable === 'no-right');
    if (closed.length < 2) throw new Error(`закрытых колонок ${closed.length} — проверять согласование числа не на чем`);

    const line = narrow.getByText(/скрыт/).first();
    await line.waitFor({ timeout: 5000 });
    states.noRight = flat(await line.innerText());
    const want = new RegExp(`^${closed.length} колон\\S+ скрыт\\S*: нет права на суммы \\(costs\\.invoice\\.read\\)$`);
    if (!want.test(states.noRight)) throw new Error(`строка над таблицей: «${states.noRight}»`);

    // Самих колонок в таблице нет — о них говорит строка; остальные на месте.
    const shown = (await headers(narrow).allInnerTexts()).map(flat);
    for (const c of closed)
      if (shown.includes(c.label)) throw new Error(`закрытая колонка «${c.label}» нарисована в таблице`);
    if (!shown.includes('Номер счёта')) throw new Error(`открытой колонки «Номер счёта» в таблице нет: ${shown}`);
  });

  await check('hidden-column-names-the-right: в реестре без права на суммы итоговой строки нет — а не пустая полоса', async () => {
    await openRegistry(narrow, { filter: seeded });
    await rowsBecome(narrow, TABLE_SEED.count, 'посеянные счета в реестре у человека без права на суммы');
    // Реестр ставит итог под обе суммы, и обе закрыты: считать нечего и показывать нечего.
    if ((await narrow.locator('tfoot').count()) !== 0)
      throw new Error(`итоговая строка нарисована: «${flat(await narrow.locator('tfoot').innerText())}»`);
  });
  await narrow.close();

  await check('hidden-column-names-the-right: у администратора строки нет, а суммы — колонкой', async () => {
    await open(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета у администратора');

    const shown = (await headers(page).allInnerTexts()).map(flat);
    if (!shown.includes('Сумма к оплате')) throw new Error(`колонки сумм у администратора нет: ${shown}`);
    if ((await page.getByText(/скрыт/).count()) !== 0)
      throw new Error('администратору показана строка о скрытых колонках');
  });

  await check('table-states-are-five-and-different: пять состояний — пять разных текстов', async () => {
    const names = ['noData', 'filteredOut', 'noRight', 'removed', 'moduleOff'];
    const missing = names.filter(n => !states[n]);
    if (missing.length) throw new Error(`состояния не получены: ${missing.join(', ')}`);
    for (let i = 0; i < names.length; i++)
      for (let j = i + 1; j < names.length; j++)
        if (states[names[i]] === states[names[j]])
          throw new Error(`«${names[i]}» и «${names[j]}» говорят одно и то же: «${states[names[i]]}»`);
  });

  // ── 3. Состояние в адресе ─────────────────────────────────────────────────────────────────────

  const addChip = chips(page).getByRole('button', { name: 'условие', exact: true });
  const editor = page.getByRole('dialog');
  const chipCount = () => chips(page).getByRole('button', { name: /^Снять условие/ }).count();
  const sortedBy = async () => flat(await page.locator('thead th[aria-sort]:not([aria-sort="none"])').allInnerTexts()
    .then(t => t.join('|')));
  const columnsButton = page.getByRole('button', { name: /^Колонки/ });

  await check('view-state-survives-reload-and-back: отбор, сортировка и колонки переживают перезагрузку', async () => {
    // Сначала — другая страница приложения: «назад» из таблицы обязано куда-то уйти.
    await page.goto(`${BASE}/costs/invoices`, { waitUntil: 'networkidle' });
    await page.goto(TABLE, { waitUntil: 'networkidle' });
    await addChip.waitFor({ timeout: 10000 });

    // Отбор А, под ним — сортировка и убранная колонка.
    await addChip.click();
    await editor.getByLabel('Колонка').selectOption({ label: 'Назначение' });
    await editor.getByLabel('Значение').fill(TABLE_SEED.purpose);
    await editor.getByRole('button', { name: 'Добавить' }).click();
    await rowsBecome(page, TABLE_SEED.count, 'отбор по назначению посева');

    await page.getByRole('button', { name: 'Дата счёта' }).click();
    await columnsButton.click();
    // Щелчок, а не uncheck(): состояние галочки — это адрес страницы, и меняется оно следующим
    // кадром; uncheck() сверяет галочку в том же кадре и считает щелчок несостоявшимся.
    await page.getByRole('dialog').getByLabel('Плательщик', { exact: true }).click();
    await page.waitForFunction(() => ![...document.querySelectorAll('thead th')].some(th => th.textContent.trim() === 'Плательщик'),
      null, { timeout: 5000 }).catch(() => { throw new Error('колонка «Плательщик» после снятия галочки осталась'); });
    await page.keyboard.press('Escape');

    // Отбор Б поверх А, под ним — другая сортировка.
    await addChip.click();
    await editor.getByLabel('Колонка').selectOption({ label: 'Состояние оплаты' });
    await editor.getByLabel('Значение').selectOption({ label: 'Не оплачен' });
    await editor.getByRole('button', { name: 'Добавить' }).click();
    await page.waitForFunction(() => location.hash.includes('%D0%9D%D0%B5%20%D0%BE%D0%BF%D0%BB%D0%B0%D1%87%D0%B5%D0%BD'));
    await page.getByRole('button', { name: 'Номер счёта' }).click();
    await page.waitForFunction(() => decodeURIComponent(location.hash).includes('sort=Номер:asc'));

    await page.reload({ waitUntil: 'networkidle' });
    await rowsBecome(page, TABLE_SEED.count, 'после перезагрузки под двумя условиями');
    if ((await chipCount()) !== 2) throw new Error(`после перезагрузки чипов ${await chipCount()}, а условий два`);
    if ((await sortedBy()) !== 'Номер счёта') throw new Error(`после перезагрузки сортировка: «${await sortedBy()}»`);
    if ((await headers(page).allInnerTexts()).map(flat).includes('Плательщик'))
      throw new Error('убранная колонка вернулась после перезагрузки');
  });

  await check('view-state-survives-reload-and-back: «назад» возвращает предыдущий отбор за один шаг', async () => {
    // Шаг назад — отбор А с ЕГО сортировкой и колонками, а не «Б без последней сортировки».
    await page.goBack();
    await page.waitForFunction(() => !location.hash.includes('%D0%9D%D0%B5%20%D0%BE%D0%BF%D0%BB%D0%B0%D1%87%D0%B5%D0%BD'),
      null, { timeout: 5000 }).catch(() => { throw new Error('после «назад» второе условие осталось в адресе'); });
    await rowsBecome(page, TABLE_SEED.count, 'после «назад» под первым условием');
    if ((await chipCount()) !== 1) throw new Error(`после «назад» чипов ${await chipCount()}, а обязан остаться один`);
    if ((await sortedBy()) !== 'Дата счёта') throw new Error(`после «назад» сортировка: «${await sortedBy()}»`);
    if ((await headers(page).allInnerTexts()).map(flat).includes('Плательщик'))
      throw new Error('после «назад» убранная колонка вернулась');

    // Ещё шаг — таблица без отбора; третий — уже другая страница. Шагов ровно столько, сколько отборов.
    await page.goBack();
    await page.waitForFunction(() => location.hash === '', null, { timeout: 5000 })
      .catch(() => { throw new Error(`второй шаг назад не вернул таблицу без отбора: ${page.url()}`); });
    if ((await chipCount()) !== 0) throw new Error('таблица без отбора показывает чипы');

    await page.goBack();
    await page.waitForFunction(() => !location.pathname.startsWith('/tables/'), null, { timeout: 5000 })
      .catch(() => { throw new Error(`третий шаг назад остался в таблице: ${page.url()} — в историю легло лишнее`); });
  });

  await check('view-state-survives-reload-and-back: два действия быстрее перерисовки складываются, а не стирают друг друга', async () => {
    await open(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета');

    // Оба щелчка — ОДНОЙ задачей браузера: между ними экран перерисоваться не может, и второй
    // заведомо сделан по состоянию, нарисованному до первого. Так человек щёлкает по шапке сразу
    // после чипа; у него это вопрос везения, а здесь — нет. Двумя шагами прогона то же самое
    // проверялось через раз (на дев-стенде) либо ни разу (в CI): перерисовка обычно успевала.
    // Ломается: считать изменение от нарисованного состояния — щелчок по строке сотрёт сортировку.
    await page.evaluate(() => {
      [...document.querySelectorAll('thead th button')].find(b => b.textContent.trim() === 'Дата счёта').click();
      [...document.querySelectorAll('tbody tr')].find(r => !r.querySelector('td[colspan]')).click();
    });
    await page.waitForFunction(() => location.hash.includes('row='), null, { timeout: 5000 })
      .catch(() => { throw new Error('щелчок по строке в адрес не попал'); });
    const address = decodeURIComponent(new URL(page.url()).hash);
    if (!address.includes('sort=Дата:asc'))
      throw new Error(`щелчок по строке стёр сортировку, поставленную мгновением раньше: «${address}»`);
    if (!address.includes(TABLE_SEED.purpose)) throw new Error(`отбор из адреса пропал: «${address}»`);
  });

  // ── 4. Итог по отбору ─────────────────────────────────────────────────────────────────────────

  await check('total-counts-the-selection-not-the-page: итог — по всему отбору, а не по странице', async () => {
    await open(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета на одной странице');

    await page.getByLabel('Строк на странице').selectOption('50');
    await rowsBecome(page, 50, 'страница из пятидесяти строк');
    await columnsButton.click();
    await page.getByRole('dialog').getByLabel('Итог по колонке «Сумма к оплате»').selectOption({ label: 'Сумма' });
    await page.keyboard.press('Escape');

    const column = (await headers(page).allInnerTexts()).map(flat).indexOf('Сумма к оплате');
    const foot = page.locator('tfoot td').nth(column);
    const want = `Σ ${tableSeedSum().toLocaleString('ru-RU')}`;
    await page.waitForFunction(([i, text]) =>
      document.querySelectorAll('tfoot td')[i]?.textContent.replace(/[  ]/g, ' ').includes(text),
      [column, flat(want)], { timeout: 8000 })
      .catch(async () => { throw new Error(`итог под «Сумма к оплате»: «${flat(await foot.innerText())}», а отбор даёт «${flat(want)}»`); });

    // Сумма строк СТРАНИЦЫ обязана быть меньше — иначе проверка зелёная и на итоге по странице.
    const cells = await dataRows(page).locator(`td:nth-child(${column + 1})`).allInnerTexts();
    const pageSum = cells.map(c => Number(flat(c).replace(/ /g, '').replace(',', '.'))).reduce((a, b) => a + b, 0);
    if (!(pageSum > 0 && pageSum < tableSeedSum()))
      throw new Error(`сумма страницы ${pageSum} не меньше суммы отбора ${tableSeedSum()} — проверять не на чем`);
    if (!flat(await page.getByText(/^Строки /).innerText()).startsWith(`Строки 1–50 из ${TABLE_SEED.count}`))
      throw new Error(`подпись страницы: «${flat(await page.getByText(/^Строки /).innerText())}»`);

    // На второй странице — остаток строк и ТОТ ЖЕ итог.
    await page.getByRole('button', { name: 'Следующая страница' }).click();
    await rowsBecome(page, TABLE_SEED.count - 50, 'вторая страница');
    if (!flat(await foot.innerText()).includes(flat(want)))
      throw new Error(`на второй странице итог стал «${flat(await foot.innerText())}»`);
  });

  // ── 5. Негодный отбор ─────────────────────────────────────────────────────────────────────────

  await check('broken-filter-refuses-visibly: условие по колонке, которой нет, — видимый отказ с её именем', async () => {
    await open(page, { filter: condition('КолонкиТакойНет', 'eq', 'x') });

    // Отказ СЕРВЕРА, а не оговорка чипа: чип предупреждает о негодном условии и сам, до всякого
    // запроса, — и на экране, съевшем отказ, его оговорка осталась бы единственным текстом с этим именем.
    const refusal = page.getByRole('alert').filter({ hasText: 'КолонкиТакойНет' }).filter({ hasText: 'Строки не отданы' });
    await refusal.waitFor({ timeout: 8000 })
      .catch(() => { throw new Error('отказа сервера с названием колонки на экране нет'); });
    if ((await page.locator('tbody tr').count()) !== 0)
      throw new Error('под отказом стоит таблица — отказ выдан за выдачу');
    if ((await page.getByText('Отбор ничего не нашёл').count()) !== 0)
      throw new Error('отказ показан как «отбор ничего не нашёл»');
    // Экран остался экраном: название таблицы и чип, о котором отказ говорит, — на месте.
    if ((await chipCount()) !== 1) throw new Error('чип негодного условия пропал — исправить его нечем');
  });

  await check('broken-filter-refuses-visibly: отбор, который не разбирается, отказывает, а не показывает все строки', async () => {
    await open(page, { filter: '{"type":"condi' });

    const refusal = page.getByRole('alert').filter({ hasText: /отбор/i }).first();
    await refusal.waitFor({ timeout: 8000 });
    if ((await page.locator('tbody tr').count()) !== 0)
      throw new Error('неразобранный отбор выброшен, и показаны все строки');

    await refusal.getByRole('button', { name: 'Снять отбор' }).click();
    await page.waitForFunction(
      () => [...document.querySelectorAll('tbody tr')].filter(r => !r.querySelector('td[colspan]')).length > 1,
      null, { timeout: 8000 }).catch(() => { throw new Error('после «Снять отбор» строки не пришли'); });
  });

  // ── Строка в боковой панели и закреплённая колонка ────────────────────────────────────────────

  await check('строка открывается в панели со ВСЕМИ колонками и переживает перезагрузку', async () => {
    await open(page, { filter: seeded, columns: 'Номер,Дата', sort: 'Номер:asc' });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета в двух колонках');

    await dataRows(page).first().click();
    const panel = page.getByRole('complementary', { name: 'Строка: счёт' });
    await panel.waitFor({ timeout: 5000 });
    // В таблице две колонки, а в панели — все: назначение там есть, хотя колонкой оно не показано.
    await panel.getByText(TABLE_SEED.purpose).waitFor({ timeout: 5000 });
    await panel.getByText('ИТОГ-01', { exact: true }).waitFor();
    if (!page.url().includes('row=')) throw new Error('открытая строка в адрес не попала');

    await page.reload({ waitUntil: 'networkidle' });
    await panel.getByText('ИТОГ-01', { exact: true }).waitFor({ timeout: 8000 });

    await panel.getByRole('button', { name: 'Закрыть строку' }).click();
    await panel.waitFor({ state: 'detached', timeout: 5000 });
    if (page.url().includes('row=')) throw new Error('панель закрыта, а строка осталась в адресе');
  });

  await check('закреплённая колонка остаётся на месте при прокрутке вбок', async () => {
    await open(page, { filter: seeded, pin: '1' });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета');
    await page.setViewportSize({ width: 900, height: 1000 });

    // И шапка, и клетка строки: шапка липкая сама по себе (она держится у верхнего края), и
    // закрепление, сломанное в строках, по одной шапке осталось бы незамеченным.
    const first = headers(page).first();
    const cell = dataRows(page).first().locator('td').first();
    const before = { head: (await first.boundingBox()).x, cell: (await cell.boundingBox()).x };
    const moved = await page.locator('table').evaluate(t => {
      t.parentElement.scrollLeft = 400;
      return t.parentElement.scrollLeft;
    });
    if (moved < 100) throw new Error('таблица вбок не прокручивается — проверять закрепление не на чем');
    const after = { head: (await first.boundingBox()).x, cell: (await cell.boundingBox()).x };
    if (Math.abs(after.head - before.head) > 1) throw new Error(`закреплённый заголовок уехал на ${before.head - after.head} px`);
    if (Math.abs(after.cell - before.cell) > 1) throw new Error(`закреплённая клетка уехала на ${before.cell - after.cell} px`);
    const second = (await headers(page).nth(1).boundingBox()).x;
    if (second >= before.head + (await first.boundingBox()).width)
      throw new Error('вторая колонка не ушла под закреплённую — прокрутки не было');
  });

  // ── 6. Готовое представление «Реестр счетов» ──────────────────────────────────────────────────

  const REGISTRY_COLUMNS = [
    'Поставщик', 'Сумма', 'Сумма к оплате', 'Номер счёта', 'Дата счёта', 'Дата отгрузки', 'Отсрочка, дней',
    'Оплатить до', 'Осталось дней', 'Состояние оплаты', 'Оплачен', 'Учётный период',
    'Суммы по периодам', 'Объект', 'Плательщик', 'Назначение', 'Строк без позиции',
  ];
  const OFFERED = ['Дата счёта', 'Плательщик', 'Поставщик', 'Объект', 'Состояние оплаты', 'Учётный период'];
  const AXIS = 'период — по дате счёта, не по оплате';
  const titles = async p => (await headers(p).allInnerTexts()).map(flat);

  async function openRegistry(p, state = {}) {
    await p.goto('about:blank');
    await p.goto(`${TABLE}/registry` + (Object.keys(state).length ? hash(state) : ''), { waitUntil: 'networkidle' });
    await p.getByRole('heading', { name: 'Реестр счетов' }).waitFor({ timeout: 10000 });
  }

  await check('registry-is-a-named-setup: пункт навигации открывает реестр его колонками, итогами и местами под отбор', async () => {
    await page.setViewportSize({ width: 1500, height: 1000 });
    await page.goto(`${BASE}/document-sets`, { waitUntil: 'networkidle' });
    await page.getByRole('link', { name: 'Реестр счетов' }).click();
    await page.getByRole('heading', { name: 'Реестр счетов' }).waitFor({ timeout: 10000 });
    await dataRows(page).first().waitFor({ timeout: 8000 });

    const url = new URL(page.url());
    if (url.pathname !== '/tables/costs.invoices/registry') throw new Error(`пункт ведёт на ${url.pathname}`);
    if (url.hash) throw new Error(`нетронутое представление записало в адрес «${decodeURIComponent(url.hash)}»`);

    const shown = await titles(page);
    if (shown.join(' | ') !== REGISTRY_COLUMNS.join(' | '))
      throw new Error(`колонки реестра: ${shown.join(' | ')}`);

    // Итог — под обеими суммами: под отбором по объекту первая становится долей, вторая остаётся счётом.
    for (const title of ['Сумма', 'Сумма к оплате']) {
      const foot = flat(await page.locator('tfoot td').nth(shown.indexOf(title)).innerText());
      if (!foot.startsWith('Σ ')) throw new Error(`под «${title}» итога нет: «${foot}»`);
    }

    // Места под отбор предложены, но условием ни одно не стало: отбор ставит человек.
    for (const name of OFFERED)
      if ((await chips(page).getByRole('button', { name, exact: true }).count()) !== 1)
        throw new Error(`места под отбор «${name}» нет`);
    if ((await chips(page).getByRole('button', { name: /^Снять условие/ }).count()) !== 0)
      throw new Error('реестр открылся с условием отбора, которого человек не ставил');

    // Место — ещё не условие: без значения оно отбором не становится ни кнопкой, ни клавишей Enter.
    // У поставщика пустое «равно» сервер принял бы и молча отобрал бы счета без поставщика.
    await chips(page).getByRole('button', { name: 'Поставщик', exact: true }).click();
    const add = page.getByRole('button', { name: 'Добавить', exact: true });
    await add.waitFor({ timeout: 5000 });
    if (!(await add.isDisabled())) throw new Error('пустое место под «Поставщика» можно добавить условием');
    // Enter — из поля значения: пока кнопка заперта, форму он не отправляет.
    await page.locator('[data-radix-popper-content-wrapper] input').last().press('Enter');
    await page.keyboard.press('Escape');
    // «Не стало» читают, когда экран доработал: в первый же миг условия нет и у сломанного места.
    await settled(page);
    if ((await chips(page).getByRole('button', { name: /^Снять условие/ }).count()) !== 0 || new URL(page.url()).hash)
      throw new Error('пустое место стало условием отбора по клавише Enter');
  });

  await check('registry-address-holds-only-the-difference: правка ложится в адрес отличием и переживает перезагрузку', async () => {
    await openRegistry(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета под реестром');

    await columnsButton.click();
    await page.getByRole('dialog').getByLabel('Назначение', { exact: true }).click();
    await page.keyboard.press('Escape');
    await page.waitForFunction(n => document.querySelectorAll('thead th').length === n, REGISTRY_COLUMNS.length - 1,
      { timeout: 5000 }).catch(() => { throw new Error('колонка со снятой галочкой осталась в реестре'); });

    const params = [...new URLSearchParams(new URL(page.url()).hash.slice(1)).keys()].sort();
    if (params.join(',') !== 'columns,filter')
      throw new Error(`в адресе не одно отличие, а «${params.join(', ')}» — настройка представления записана целиком`);

    await page.reload({ waitUntil: 'networkidle' });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета после перезагрузки');
    const after = await titles(page);
    if (after.join(' | ') !== REGISTRY_COLUMNS.filter(c => c !== 'Назначение').join(' | '))
      throw new Error(`после перезагрузки колонки: ${after.join(' | ')}`);
    // Итоги и закрепление в адрес не писались — значит, пришли из представления, а не из умолчаний таблицы.
    if (!flat(await page.locator('tfoot td').nth(after.indexOf('Сумма к оплате')).innerText()).startsWith('Σ '))
      throw new Error('после перезагрузки итог реестра пропал — адрес прочитан от умолчаний таблицы');
  });

  await check('registry-total-names-its-axis: под отбором периода сумма и её итог называют ось', async () => {
    const period = { type: 'condition', column: 'Дата', op: 'between', values: ['2026-09-01', '2026-09-30'] };
    await openRegistry(page, { filter: { type: 'group', logic: 'and', children: [seeded, period] } });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета за сентябрь');

    const shown = await titles(page);
    const column = shown.findIndex(title => title.startsWith('Сумма ('));
    if (column < 0 || !shown[column].includes(AXIS)) throw new Error(`шапка суммы оси не называет: ${shown.join(' | ')}`);

    // Итог — та строка, которую читают как «столько потрачено»; шапка к этому моменту уехала вверх.
    const foot = flat(await page.locator('tfoot td').nth(column).innerText());
    if (!foot.includes(`Σ ${flat(tableSeedSum().toLocaleString('ru-RU'))}`)) throw new Error(`итог периода: «${foot}»`);
    if (!foot.includes(AXIS)) throw new Error(`итог оси не называет: «${foot}»`);
    // Ось — свойство отбора, а не одной колонки: итог «Суммы к оплате» под периодом значит то же.
    const whole = flat(await page.locator('tfoot td').nth(shown.indexOf('Сумма к оплате')).innerText());
    if (!whole.includes(AXIS)) throw new Error(`итог «Суммы к оплате» оси не называет: «${whole}»`);

    // Период стоит чипом — места под него больше нет: рядом с чипом оно читалось бы как «не задан».
    if ((await chips(page).getByRole('button', { name: 'Дата счёта', exact: true }).count()) !== 0)
      throw new Error('место «Дата счёта» осталось при стоящем условии по дате');
    if ((await chips(page).getByRole('button', { name: 'Плательщик', exact: true }).count()) !== 1)
      throw new Error('вместе с местом под дату пропали и остальные');

    // Без периода в отборе называть нечего — подписи нет ни в шапке, ни под итогом.
    await openRegistry(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета без периода');
    if ((await page.getByText(AXIS).count()) !== 0) throw new Error('ось названа, хотя период в отбор не входит');
  });

  await check('unknown-view-is-refused: представления нет — экран это говорит, а таблицу вместо него не открывает', async () => {
    await page.goto('about:blank');
    await page.goto(`${TABLE}/net-takogo`, { waitUntil: 'networkidle' });
    const alert = page.getByRole('alert').filter({ hasText: 'нет представления «net-takogo»' });
    await alert.waitFor({ timeout: 8000 });
    if ((await headers(page).count()) !== 0)
      throw new Error('под несуществующим представлением открылась таблица — её примут за то, о чём просили');
    await page.getByRole('link', { name: 'Открыть таблицу целиком' }).click();
    await page.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10000 });
  });

  await check('registry-leads-to-the-whole-table: из реестра к таблице целиком ведёт ссылка, и отбор едет с ней', async () => {
    await openRegistry(page, { filter: seeded });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета под реестром');

    await page.getByRole('link', { name: 'Таблица целиком', exact: true }).click();
    await until(() => {
      const path = new URL(page.url()).pathname;
      if (path !== '/tables/costs.invoices') throw new Error(`ссылка ведёт на ${path}`);
    });
    // Настройка представления осталась позади: колонок больше, чем в реестре. Ждём именно ЭТО, а не
    // число строк: строк в реестре и в таблице поровну, и такое ожидание сбывается до перехода —
    // колонки тогда читаются со старого ответа, пока новый в пути (на раннере так и вышло).
    await until(async () => {
      if ((await headers(page).count()) <= REGISTRY_COLUMNS.length)
        throw new Error('таблица целиком открылась колонками реестра');
    });
    // Отбор приехал: строки — уже из ответа таблицы целиком, и их столько же, сколько посеяно.
    await rowsBecome(page, TABLE_SEED.count, 'те же счета в таблице целиком');
  });
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Экран таблицы');
