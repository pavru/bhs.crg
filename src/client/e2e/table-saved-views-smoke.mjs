// Живой прогон СОХРАНЁННЫХ ПРЕДСТАВЛЕНИЙ таблицы (задача G3a этапа 2, issue #1095, ТЗ CORE-33).
//
// ЗАЧЕМ ОН ЕСТЬ. Правила — что считается изменением, какие действия показаны, что стоит под адресом —
// проверены юнит-тестами без экрана (savedViews.test.ts), отказы сервера — его тестами
// (SavedTableViewTests). Обещания задачи — про экран целиком, и у каждого названо, чем оно ломается:
//
//   1. locked-view-is-edited-only-by-copy — СТОРОЖ ЗАДАЧИ. Представление модуля заперто: под
//      изменённым «Реестром счетов» действия «Сохранить» нет вовсе, есть «Сохранить как своё»; сервер
//      на правку по коду модуля отвечает 409 и называет выход. Ломается: показать «Сохранить» у
//      представления модуля; отдать на такую правку 404 вместо «заперто».
//   2. unsaved-changes-are-marked-until-reset — СТОРОЖ ЗАДАЧИ. Изменение помечено «· изменено»,
//      пометка переживает перезагрузку и пропадает от «Сбросить» вместе с отличием в адресе.
//      Ломается: считать изменением перелистывание (помечено всегда) либо не считать сортировку.
//   3. saved-view-opens-by-address — представление сохраняется и открывается по адресу: название,
//      отбор и строки под ним — после полной перезагрузки. «Сохранить» поверх оставляет в адресе
//      только место в таблице. Ломается: отсчитывать адрес от умолчаний таблицы — отбор не придёт.
//   4. taken-name-is-refused-under-the-field — занятое название: отказ под полем, диалог остаётся.
//   5. foreign-shared-view-is-copied-not-edited — чужое общее без права: в списке оно есть, действий
//      над ним нет, под изменённым — только «Сохранить как своё».
//   6. withheld-filter-is-named — отбор автора на колонке, закрытой читателю, читателю не отдан, и
//      экран это ГОВОРИТ, а не показывает другие строки под чужим названием. Ломается: убрать строку
//      о скрытом отборе.
//   7. stale-bookmark-leads-to-the-table — удалённое представление: экран это говорит и ведёт к
//      таблице целиком с отбором из адреса, а не открывает её под видом представления.
//
// ⚠️ ДАННЫЕ — посеянные счета (e2e/seed-invoices.mjs, `TABLE_SEED`). Свои представления прогон заводит
// и убирает сам — по названию с приставкой, чтобы повторный запуск не упёрся в занятые имена.
//
// ⚠️ ЧУЖОЕ ОБЩЕЕ И СКРЫТЫЙ ОТБОР — ПЕРЕДЕЛАННЫМ ОТВЕТОМ, а не вторым человеком. Вход в приложение
// ограничен частотой, и входы всех прогонов CI обязаны помещаться в одно окно (`suites.test.mjs`):
// второй вход здесь в него не помещался. Поэтому ответ сервера о представлении берётся НАСТОЯЩИЙ и
// правится на лету — в том виде, какой сервер отдаёт читателю сам: «не моё, править нельзя» и
// «отбор не отдан, причина названа». Что сервер отдаёт именно так, стерегут его тесты
// (`SavedTableViewTests`); здесь проверяется экран. Живьём, вторым человеком, обе проверки
// пройдены один раз при разработке (PR экрана G3a).
//
// ⚠️ Элементы ищутся ПО ВИДИМОМУ ТЕКСТУ и ролям, без служебных атрибутов.
//
// Требует поднятых фронта и бэка, посеянных данных и ВКЛЮЧЁННОГО модуля `costs`.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/table-saved-views-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, until } from './harness.mjs';
import { TABLE_SEED, ensureTableRows } from './seed-invoices.mjs';

const ADDRESS = 'costs.invoices';
const TABLE = `${BASE}/tables/${ADDRESS}`;
const PREFIX = 'Прогон G3a';
const ID = /\/tables\/costs\.invoices\/([0-9a-f-]{36})$/i;

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();

const hash = state => '#' + Object.entries(state)
  .map(([k, v]) => `${k}=${encodeURIComponent(typeof v === 'string' ? v : JSON.stringify(v))}`).join('&');
const seeded = { type: 'condition', column: 'Назначение', op: 'eq', value: TABLE_SEED.purpose };

const dataRows = p => p.locator('tbody tr').filter({ hasNot: p.locator('td[colspan]') });
const title = p => p.getByRole('heading', { level: 1 });
const mark = p => p.getByText('· изменено');
const action = (p, name) => p.getByRole('button', { name, exact: true });
const list = p => p.getByRole('dialog', { name: 'Представления таблицы' });

/** Запрос тем же токеном, что у страницы; отказ возвращается кодом и текстом, а не исключением. */
function api(p, method, path, body, version) {
  return p.evaluate(async ([method, path, body, version]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const res = await fetch(`/api${path}`, {
      method,
      headers: {
        'Content-Type': 'application/json', Authorization: `Bearer ${token}`,
        ...(version === null ? {} : { 'If-Match': version }),
      },
      body: body === null ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* тело отказа бывает текстом */ }
    return { status: res.status, body: json, text };
  }, [method, path, body ?? null, version ?? null]);
}

async function must(p, method, path, body, version) {
  const answer = await api(p, method, path, body, version);
  if (answer.status >= 300) throw new Error(`${method} ${path} → ${answer.status} ${answer.text.slice(0, 200)}`);
  return answer.body;
}

/**
 * Переделать ответы сервера на время `run`: список представлений и права вошедшего. Ответ берётся
 * НАСТОЯЩИЙ и правится — так подделка не расходится с формой, которую сервер отдаёт на самом деле.
 */
async function withAnswers(p, { views, access }, run) {
  const patterns = [[`**/api/tables/${ADDRESS}/views`, views], ['**/api/account/access', access]].filter(([, change]) => change);
  for (const [pattern, change] of patterns)
    await p.route(pattern, async route => {
      if (route.request().method() !== 'GET') return route.continue();
      const response = await route.fetch();
      if (!response.ok()) return route.fulfill({ response });
      await route.fulfill({ response, json: change(await response.json()) });
    });
  try { await run(); } finally { for (const [pattern] of patterns) await p.unroute(pattern); }
}

/** Открыть адрес полной перезагрузкой: адрес обязан хватить сам. */
async function open(p, path, state = {}) {
  await p.goto('about:blank');
  await p.goto(TABLE + path + (Object.keys(state).length ? hash(state) : ''), { waitUntil: 'networkidle' });
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

async function sortBy(p, column) {
  await p.locator('thead th').getByRole('button', { name: column, exact: true }).click();
  await p.waitForFunction(() => decodeURIComponent(location.hash).includes('sort='));
}

/** Сохранить изменённое новым представлением; возвращает его идентификатор из адреса. */
async function saveAs(p, name, { shared = false } = {}) {
  await action(p, 'Сохранить как своё').click();
  const dialog = p.getByRole('dialog', { name: 'Сохранить представление' });
  await dialog.getByLabel('Название').fill(name);
  if (shared) await dialog.getByLabel(/^Общее/).check();
  await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
  let id = null;
  await until(() => {
    id = new URL(p.url()).pathname.match(ID)?.[1] ?? null;
    if (!id) throw new Error(`после сохранения адрес — ${new URL(p.url()).pathname}`);
  });
  await title(p).getByText(name, { exact: true }).waitFor({ timeout: 8000 });
  return id;
}

/** Убрать представления прогона — свои и общие, сколько их видит этот человек. */
async function sweep(p) {
  for (const view of await must(p, 'GET', `/tables/${ADDRESS}/views`))
    if (view.title.startsWith(PREFIX) && view.canEdit)
      await api(p, 'DELETE', `/tables/${ADDRESS}/views/${view.id}`, null, view.version);
}

await login(page);

try {
  await ensureTableRows((method, path, body) => must(page, method, path, body));
  await sweep(page);
} catch (e) {
  console.error(`Данных прогона нет: ${e.message}. Включён ли модуль costs у сервера `
    + '(Modules__Enabled=id,costs) и заведён ли тип счёта? Посев: npm run test:e2e:seed.');
  await browser.close();
  process.exit(1);
}

const OWN = `${PREFIX}: счета посева`;
const COMMON = `${PREFIX}: общее`;
let ownId = null;

try {
  await check('locked-view-is-edited-only-by-copy: под изменённым реестром «Сохранить» нет, сервер отвечает «заперто»', async () => {
    await open(page, '/registry');
    await title(page).getByText('Реестр счетов').waitFor({ timeout: 10000 });
    await settled(page);
    if ((await mark(page).count()) !== 0) throw new Error('нетронутый реестр помечен «изменено»');

    await sortBy(page, 'Номер счёта');
    await mark(page).waitFor({ timeout: 5000 });
    if ((await action(page, 'Сохранить').count()) !== 0)
      throw new Error('у представления модуля показано «Сохранить» — оно заперто, правится только копией');
    await action(page, 'Сохранить как своё').waitFor({ timeout: 3000 });

    // Сервер держит то же и сам: правка по коду модуля — «заперто» с выходом, а не «такого нет».
    const body = { title: 'x', shared: false, state: { pinned: 0 } };
    const refused = await api(page, 'PUT', `/tables/${ADDRESS}/views/registry`, body);
    if (refused.status !== 409 || !/как сво/i.test(refused.text))
      throw new Error(`правка представления модуля: ${refused.status} «${refused.text.slice(0, 160)}»`);
  });

  await check('saved-view-opens-by-address: копия сохраняется, получает адрес и открывается им после перезагрузки', async () => {
    await open(page, '/registry', { filter: seeded, sort: 'Номер:desc' });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета под реестром');
    ownId = await saveAs(page, OWN);
    if ((await mark(page).count()) !== 0) throw new Error('только что сохранённое помечено «изменено»');
    if (new URL(page.url()).hash !== '') throw new Error(`в адресе осталось отличие: ${new URL(page.url()).hash}`);

    await open(page, `/${ownId}`);
    await title(page).getByText(OWN, { exact: true }).waitFor({ timeout: 10000 });
    // Отбор — часть настройки: строк столько, сколько посеяно, а не все счета стенда.
    await rowsBecome(page, TABLE_SEED.count, 'счета под сохранённым отбором');
    const sorted = await page.locator('thead th[aria-sort="descending"]').allInnerTexts();
    if (!sorted.some(t => t.includes('Номер счёта'))) throw new Error(`сортировка не пришла: ${sorted}`);

    const stored = (await must(page, 'GET', `/tables/${ADDRESS}/views`)).find(v => v.id === ownId);
    if (stored.basedOn !== 'registry') throw new Error(`«из чего сделано»: ${stored.basedOn}`);
  });

  await check('unsaved-changes-are-marked-until-reset: изменение помечено, переживает перезагрузку и пропадает от сброса', async () => {
    await open(page, `/${ownId}`);
    await rowsBecome(page, TABLE_SEED.count, 'счета под сохранённым отбором');
    // Перелистывание и размер страницы — не изменение настройки.
    await page.getByLabel('Строк на странице').selectOption('50');
    await page.getByRole('button', { name: 'Следующая страница' }).click();
    await page.waitForFunction(() => location.hash.includes('page=2'));
    if ((await mark(page).count()) !== 0) throw new Error('перелистывание помечено как изменение представления');

    await sortBy(page, 'Дата счёта');
    await mark(page).waitFor({ timeout: 5000 });
    await page.reload({ waitUntil: 'networkidle' });
    await mark(page).waitFor({ timeout: 8000 });

    await action(page, 'Сбросить').click();
    await until(async () => {
      if ((await mark(page).count()) !== 0) throw new Error('после сброса пометка осталась');
      if (decodeURIComponent(new URL(page.url()).hash).includes('sort=')) throw new Error('после сброса отличие осталось в адресе');
    });
  });

  await check('saved-view-opens-by-address: «Сохранить» поверх оставляет в адресе только место в таблице', async () => {
    await open(page, `/${ownId}`);
    await rowsBecome(page, TABLE_SEED.count, 'счета под сохранённым отбором');
    await sortBy(page, 'Дата счёта');
    await action(page, 'Сохранить').click();
    await until(async () => {
      if ((await mark(page).count()) !== 0) throw new Error('после сохранения пометка осталась');
      if (new URL(page.url()).hash !== '') throw new Error(`в адресе осталось: ${new URL(page.url()).hash}`);
    });
    await open(page, `/${ownId}`);
    await page.locator('thead th[aria-sort="ascending"]').filter({ hasText: 'Дата счёта' }).waitFor({ timeout: 8000 });
  });

  await check('taken-name-is-refused-under-the-field: занятое название — отказ под полем, диалог остаётся', async () => {
    await open(page, `/${ownId}`);
    await rowsBecome(page, TABLE_SEED.count, 'счета под сохранённым отбором');
    await sortBy(page, 'Номер счёта');
    await action(page, 'Сохранить как своё').click();
    const dialog = page.getByRole('dialog', { name: 'Сохранить представление' });
    await dialog.getByLabel('Название').fill(OWN.toUpperCase());
    await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await dialog.getByText(/уже есть/).waitFor({ timeout: 5000 });
    if (!ID.test(new URL(page.url()).pathname) || !page.url().includes(ownId))
      throw new Error('после отказа экран ушёл с представления');
    await dialog.getByRole('button', { name: 'Отмена' }).click();
  });

  // ── Чужое общее и скрытый отбор — глазами читателя: ответ сервера переделан (см. шапку) ─────────

  let commonId = null;
  const SHARE = 'core.views.share';

  await check('foreign-shared-view-is-copied-not-edited: чужое общее в списке есть, действий над ним нет', async () => {
    await open(page, '', { filter: seeded, sort: 'Номер:asc' });
    await rowsBecome(page, TABLE_SEED.count, 'посеянные счета');
    // У держателя права флажок «Общее» есть — им представление и становится общим.
    commonId = await saveAs(page, COMMON, { shared: true });
    const mine = (await must(page, 'GET', `/tables/${ADDRESS}/views`)).find(v => v.id === commonId);
    if (!mine.shared || !mine.mine || !mine.canEdit) throw new Error(`своё общее: ${JSON.stringify(mine)}`);

    const foreign = list => list.map(v => (v.id === commonId ? { ...v, mine: false, canEdit: false, author: 'Автор Прогона' } : v));
    const noRight = access => ({ ...access, permissions: access.permissions.filter(code => code !== SHARE) });
    await withAnswers(page, { views: foreign, access: noRight }, async () => {
      await open(page, '');
      await title(page).getByRole('button').click();
      const row = list(page).getByRole('link', { name: new RegExp(COMMON) });
      await row.waitFor({ timeout: 8000 });
      if (!(await row.innerText()).includes('Автор Прогона')) throw new Error('у чужого общего не назван автор');
      if ((await list(page).getByRole('button', { name: `Действия с представлением «${COMMON}»` }).count()) !== 0)
        throw new Error('у чужого общего без права показано меню действий');
      await row.click();
      await title(page).getByText(COMMON, { exact: true }).waitFor({ timeout: 8000 });
      await rowsBecome(page, TABLE_SEED.count, 'счета под чужим общим представлением');

      await sortBy(page, 'Дата счёта');
      await mark(page).waitFor({ timeout: 5000 });
      if ((await action(page, 'Сохранить').count()) !== 0) throw new Error('чужое общее без права предлагает «Сохранить»');
      // Флажка «Общее» у человека без права нет вовсе.
      await action(page, 'Сохранить как своё').click();
      const dialog = page.getByRole('dialog', { name: 'Сохранить представление' });
      await dialog.getByLabel('Название').waitFor({ timeout: 3000 });
      if ((await dialog.getByLabel(/^Общее/).count()) !== 0) throw new Error('без права на общие показан флажок «Общее»');
      await dialog.getByRole('button', { name: 'Отмена' }).click();
    });
  });

  await check('withheld-filter-is-named: отбор по закрытой колонке читателю не отдан, и экран это говорит', async () => {
    const WITHHELD = 'Отбор автора скрыт: он стоит на колонке «Сумма», а она закрыта — нет права на суммы.';
    const hidden = list => list.map(v => (v.id === commonId ? { ...v, state: { ...v.state, filter: null }, withheld: WITHHELD } : v));
    await withAnswers(page, { views: hidden }, async () => {
      await open(page, `/${commonId}`);
      await title(page).getByText(COMMON, { exact: true }).waitFor({ timeout: 10000 });
      await page.getByText(`${WITHHELD} Представление открыто без него.`).waitFor({ timeout: 5000 });
      if ((await mark(page).count()) !== 0) throw new Error('представление без отбора автора помечено «изменено»');

      // Право править есть, а «Сохранить» нет: правка заменила бы настройку целиком и стёрла отбор автора.
      await sortBy(page, 'Дата счёта');
      await mark(page).waitFor({ timeout: 5000 });
      if ((await action(page, 'Сохранить').count()) !== 0) throw new Error('под скрытым отбором показано «Сохранить»');
      await title(page).getByRole('button').click();
      await list(page).getByRole('button', { name: `Действия с представлением «${COMMON}»` }).click();
      const items = await page.getByRole('menuitem').allInnerTexts();
      if (items.join('|') !== 'Удалить') throw new Error(`под скрытым отбором действия: ${items.join(', ')}`);
      await page.keyboard.press('Escape');
    });
  });

  await check('stale-bookmark-leads-to-the-table: удалённое представление — сказано, и ссылка ведёт к таблице с отбором', async () => {
    await open(page, `/${commonId}`);
    await title(page).getByText(COMMON, { exact: true }).waitFor({ timeout: 10000 });
    await title(page).getByRole('button').click();
    await list(page).getByRole('button', { name: `Действия с представлением «${COMMON}»` }).click();
    await page.getByRole('menuitem', { name: 'Удалить' }).click();
    await page.getByRole('button', { name: 'Удалить представление' }).click();
    // Удалено открытое — экран уходит к таблице целиком, с тем же отбором.
    await until(() => {
      if (new URL(page.url()).pathname !== `/tables/${ADDRESS}`) throw new Error(`после удаления адрес — ${page.url()}`);
    });
    await rowsBecome(page, TABLE_SEED.count, 'те же счета в таблице целиком');

    // Закладка на удалённое, со своим отбором в адресе.
    await open(page, `/${commonId}`, { filter: seeded });
    const alert = page.getByRole('alert');
    await alert.getByText('Представление удалено или больше не общее.').waitFor({ timeout: 8000 });
    if ((await page.locator('thead th').count()) !== 0) throw new Error('под удалённым представлением открылась таблица');
    await alert.getByRole('link', { name: 'Открыть таблицу целиком' }).click();
    await title(page).getByText('Счета на оплату').waitFor({ timeout: 8000 });
    await rowsBecome(page, TABLE_SEED.count, 'счета под отбором из закладки');
  });
} finally {
  await sweep(page).catch(() => {});
  await browser.close();
}

process.exitCode = summarize('Сохранённые представления таблицы');
