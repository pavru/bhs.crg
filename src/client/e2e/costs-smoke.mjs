// Живой прогон модуля «Счета и накладные» ПОД ЕГО РОЛЯМИ — снабженцем и бухгалтером (N1, issue #1102).
//
// ЗАЧЕМ ОН ЕСТЬ. У модуля восемь объявленных прав, а остальные его прогоны ходят администратором, у
// которого есть всё: разницы между «право выдано» и «право не нужно» он не видит ВОВСЕ. Это дословно
// урок #974/#975, ради которого заведён `limited-smoke`, — и повторить его на модуле про деньги нельзя.
// Здесь путь счёта проходят те, кто пройдёт его на деле: снабженец заводит и разносит, бухгалтер
// отмечает оплату — и каждому доступно своё, а чужого действия на экране нет.
//
// ЧТО ПРОВЕРЯЕТСЯ — РАБОТОСПОСОБНОСТЬ И ГРАНИЦА МЕЖДУ ДВУМЯ РОЛЯМИ. Всё, на что роль имеет право,
// обязано открываться и работать, и ни один запрос не должен ответить отказом там, где интерфейс
// показывает действие. Отказ, пришедший на видимую кнопку, — дефект, даже если сам отказ «правильный».
//
// ⚠️ ЧТО КАКОЙ РОЛИ ПОЛОЖЕНО, ЗАПИСАНО ЗДЕСЬ РУКАМИ — не выведено из `/api/account/access`. Выведенное
// подстроилось бы под любую потерю права: сняли у снабженца разноску — выбор объекта исчез из ожиданий
// ВМЕСТЕ с экраном, и прогон остался бы зелёным, проверив пустоту. Здесь — самостоятельное утверждение о
// ролях (ТЗ COST-28): снабженец разносит и не платит, бухгалтер платит и не разносит.
//
// Единственный отказ, который прогон ЗОВЁТ САМ, — отметка оплаты токеном снабженца: кнопки у него нет,
// но спрятанная кнопка не доказывает, что сервер закрыт. Этот запрос в счёт отказов не идёт.
//
// Пишет: один счёт с уникальным номером на запуск (заводит снабженец, оплачивает бухгалтер). Счета
// копятся — проверки от их числа не зависят: счёт ищется по своему номеру.
//
// Требует поднятых фронта и бэка, посева (снабженец и бухгалтер — e2e/seed.mjs) и ВКЛЮЧЁННОГО модуля
// `costs` — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/costs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled } from './harness.mjs';

const SUPPLIER_EMAIL = process.env.SMOKE_SUPPLIER_EMAIL || 'snab@bhs.local';
const ACCOUNTANT_EMAIL = process.env.SMOKE_ACCOUNTANT_EMAIL || 'buh@bhs.local';
const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
// Стройку заводит посев: у снабженца права заводить стройки нет, и не должно быть.
const SITE = 'Демо-стройка';

const browser = await launchBrowser();
const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);
const number = `РОЛ-${stamp}`;

/**
 * Страница одной роли: свой контекст (своя сессия) и свой счёт отказов — с адресом и экраном, на
 * котором отказ пришёл. Собираем ВСЕ, а не падаем на первом: один неверно закрытый адрес отвечает
 * отказом на каждом экране, и список «где именно» отличает общий адрес от одного экрана.
 */
async function seat(who) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
  const page = await context.newPage();
  page.on('pageerror', e => console.log(`  ! ошибка страницы (${who}):`, e.message));
  const seat = { page, who, screen: 'вход', denials: [], expected: null };
  page.on('response', r => {
    if (r.status() !== 403) return;
    const url = new URL(r.url());
    if (seat.expected && url.pathname.endsWith(seat.expected)) return;
    seat.denials.push(`${r.request().method()} ${url.pathname}${url.search} — экран «${seat.screen}»`);
  });
  return seat;
}

/** Запрос токеном открытой страницы; отказ не бросается — возвращается кодом. */
const call = (page, method, path, body) => page.evaluate(async ([method, path, body]) => {
  const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
  const res = await fetch(`/api${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: body === null ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, body: text && res.ok ? JSON.parse(text) : text };
}, [method, path, body ?? null]);

async function api(page, method, path, body) {
  const answer = await call(page, method, path, body);
  if (answer.status >= 400) throw new Error(`${method} ${path} → ${answer.status} ${String(answer.body).slice(0, 200)}`);
  return answer.body;
}

/** Пункты бокового меню — то, что роль видит входом в модуль. */
const menu = page => page.locator('nav').first().innerText();

async function openInvoice(seat) {
  seat.screen = 'счета';
  await seat.page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await seat.page.getByRole('button', { name: new RegExp(number) }).first().click();
  seat.screen = 'счёт';
  await settled(seat.page);
}

async function openRegistry(seat) {
  seat.screen = 'реестр счетов';
  const filter = { type: 'condition', column: 'Номер', op: 'eq', value: number };
  await seat.page.goto('about:blank');
  await seat.page.goto(`${BASE}/tables/costs.invoices/registry#filter=${encodeURIComponent(JSON.stringify(filter))}`,
    { waitUntil: 'networkidle' });
  const row = seat.page.locator('tbody tr').filter({ hasText: number }).first();
  await row.waitFor({ timeout: 10_000 });
  return (await row.innerText()).replace(/[  ]/g, ' ');
}

/** Сумма счёта прогона в строке реестра. Скрытая правом колонка числа не показала бы. */
const showsAmount = row => row.split('	').some(cell => /^100([.,]00)?$/.test(cell.trim()));

/** Роль — не администратор: иначе прогон второй раз проверил бы администратора. Спрашиваем сервер (AUTH-14). */
async function notAdministrator(seat) {
  seat.screen = 'проверка учётной записи';
  const access = await api(seat.page, 'GET', '/account/access');
  const granted = access.permissions ?? [];
  if (granted.includes('core.users.manage'))
    throw new Error(`${seat.who} вошёл с правами администратора — проверки ниже ничего не значат`);
  if (!(access.modules ?? []).some(m => (m.code ?? m) === 'costs'))
    throw new Error(`${seat.who} не видит модуль costs: ${JSON.stringify(access.modules)}`);
}

function noDenials(seat) {
  if (seat.denials.length)
    throw new Error(`запросов с отказом доступа у роли «${seat.who}»: ${seat.denials.length}\n      `
      + seat.denials.join('\n      '));
}

const supplier = await seat('снабженец');
const accountant = await seat('бухгалтер');

try {
await login(supplier.page, SUPPLIER_EMAIL, PASSWORD);
await supplier.page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });

if ((await supplier.page.getByRole('heading', { name: 'Счета на оплату' }).count()) === 0) {
  console.error('Экрана счетов у снабженца нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs) '
    + 'и заведён ли снабженец посевом? Без этого проверять нечего, и зелёный прогон был бы отчётом о '
    + 'работе, которой не было.');
  process.exit(1);
}

// ══ Снабженец ═════════════════════════════════════════════════════════════════════════════════════
await check('supplier-is-not-an-administrator', () => notAdministrator(supplier));

// Счёт заводит САМ снабженец, своим токеном: подготовка администратором спрятала бы отказ на первом же шаге.
let invoiceId = null;
let site = null;
await check('supplier-enters-an-invoice', async () => {
  supplier.screen = 'ввод счёта';
  site = (await api(supplier.page, 'GET', '/constructions')).find(c => c.name === SITE);
  if (!site) throw new Error(`стройки «${SITE}» нет — посев не отработал`);
  const organizations = await api(supplier.page, 'GET', '/costs/organizations');
  if (organizations.length === 0) throw new Error('в справочнике нет ни одной организации — посев не отработал');

  const created = await api(supplier.page, 'POST', '/costs/invoices', {
    requisites: {
      'Номер': number, 'Дата': '2026-09-01', 'Итого': 100,
      'Поставщик': { $ref: 'catalog', entryId: organizations[0].id },
    },
  });
  await api(supplier.page, 'PUT', `/costs/invoices/${created.id}/lines`, {
    lines: [{ supplierText: 'Услуга прогона ролей', amount: 100 }],
  });
  invoiceId = created.id;
});

if (invoiceId === null) {
  console.error('Снабженец не смог завести счёт — остальные проверки стоят на этом счёте.');
} else {

await check('supplier-opens-the-registry', async () => {
  const row = await openRegistry(supplier);
  if (!showsAmount(row)) throw new Error(`снабженец не видит сумму счёта в реестре: ${row.slice(0, 200)}`);

  const items = await menu(supplier.page);
  for (const label of ['Счета', 'Реестр счетов'])
    if (!items.includes(label)) throw new Error(`в меню снабженца нет пункта «${label}»`);
  // Отчёт по затратам и закрытие периода — бухгалтеру: пункт без права был бы дверью в отказ.
  for (const label of ['Затраты по стройке', 'Учётный период'])
    if (items.includes(label)) throw new Error(`в меню снабженца есть пункт «${label}» — права на него у роли нет`);
});

await check('supplier-allocates-the-invoice', async () => {
  await openInvoice(supplier);
  supplier.screen = 'счёт: разноска на объект';
  const select = supplier.page.getByLabel('Объект счёта', { exact: true });
  if ((await select.count()) === 0)
    throw new Error('на экране счёта нет выбора «Объект счёта»: снабженцу нечем разнести счёт');

  await Promise.all([
    supplier.page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${invoiceId}/allocation`)
      && r.request().method() === 'PUT' && r.ok(), { timeout: 10_000 }),
    select.selectOption({ label: SITE }),
  ]);
  await supplier.page.getByText('весь счёт на этот объект').waitFor({ timeout: 10_000 });

  const part = (await api(supplier.page, 'GET', `/costs/invoices/${invoiceId}`)).lines[0].allocation.parts[0];
  if (part?.constructionId !== site.id) throw new Error(`разноска не записана на стройку: ${JSON.stringify(part)}`);
});

await check('supplier-cannot-mark-payment', async () => {
  await openInvoice(supplier);
  supplier.screen = 'счёт: оплата';
  for (const name of ['Отметить оплату', 'Отменить оплату'])
    if (await supplier.page.getByRole('button', { name }).count())
      throw new Error(`снабженцу видна кнопка «${name}» — права на оплату у роли нет`);

  // Спрятанная кнопка не доказывает, что сервер закрыт: спрашиваем его самого. Этот отказ — ожидаемый.
  supplier.expected = '/paid/preview';
  const answer = await call(supplier.page, 'POST', `/costs/invoices/${invoiceId}/paid/preview`, { paidOn: null });
  supplier.expected = null;
  if (answer.status !== 403)
    throw new Error(`сервер ответил снабженцу на расклад оплаты ${answer.status}, а обязан отказать (403)`);
});

await check('supplier-no-request-was-refused', () => noDenials(supplier));

// ══ Бухгалтер ═════════════════════════════════════════════════════════════════════════════════════
await login(accountant.page, ACCOUNTANT_EMAIL, PASSWORD);
// Стартовой странице дают догрузиться: отказы считаются с входа, а оборванный переходом запрос не посчитан.
await accountant.page.goto(`${BASE}/document-sets`, { waitUntil: 'networkidle' });

await check('accountant-is-not-an-administrator', () => notAdministrator(accountant));

await check('accountant-opens-the-registry-and-the-report', async () => {
  const row = await openRegistry(accountant);
  if (!showsAmount(row)) throw new Error(`бухгалтер не видит сумму счёта в реестре: ${row.slice(0, 200)}`);

  const items = await menu(accountant.page);
  for (const label of ['Счета', 'Реестр счетов', 'Затраты по стройке', 'Учётный период'])
    if (!items.includes(label)) throw new Error(`в меню бухгалтера нет пункта «${label}»`);

  accountant.screen = 'затраты по стройке';
  await accountant.page.goto(`${BASE}/site-costs`, { waitUntil: 'networkidle' });
  await accountant.page.locator('main').getByRole('heading', { name: /Затраты по стройке/ }).waitFor({ timeout: 10_000 });
  await settled(accountant.page);
});

await check('accountant-reads-the-allocation-and-cannot-change-it', async () => {
  await openInvoice(accountant);
  accountant.screen = 'счёт: разноска';
  if (await accountant.page.getByLabel('Объект счёта', { exact: true }).count())
    throw new Error('бухгалтеру виден выбор «Объект счёта» — права на разноску у роли нет');
  // Читать разноску он обязан: по ней считаются затраты, которые он закрывает.
  await accountant.page.getByRole('button', { name: 'разноска по объектам' }).waitFor({ timeout: 10_000 });
  if (!(await accountant.page.locator('main').innerText()).includes(SITE))
    throw new Error(`бухгалтер не видит, на какой объект разнесён счёт («${SITE}»)`);
});

await check('accountant-marks-payment', async () => {
  await openInvoice(accountant);
  accountant.screen = 'счёт: оплата';
  const mark = accountant.page.getByRole('button', { name: 'Отметить оплату' });
  if ((await mark.count()) === 0)
    throw new Error('на экране счёта нет кнопки «Отметить оплату»: бухгалтеру нечем отметить оплату');
  await mark.click();
  const dialog = accountant.page.getByRole('dialog');
  // Дату сервер предлагает сам (сегодня). Подпись кнопки зависит от закрытых периодов стенда:
  // «Отметить оплату» либо «…с переносом» — годятся обе, отказ вместо формы — нет.
  const pay = dialog.getByRole('button', { name: /^Отметить оплату/ });
  await pay.waitFor({ timeout: 10_000 });
  await settled(accountant.page);
  if (await pay.isDisabled()) throw new Error(`оплата недоступна: ${(await dialog.innerText()).slice(0, 300)}`);

  await Promise.all([
    accountant.page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${invoiceId}/paid`)
      && r.request().method() === 'POST' && r.ok(), { timeout: 10_000 }),
    pay.click(),
  ]);
  await accountant.page.getByText(/Оплачен \d{2}\.\d{2}\.\d{4}/).first().waitFor({ timeout: 10_000 });

  const view = await api(accountant.page, 'GET', `/costs/invoices/${invoiceId}`);
  if (!view.payment?.paid) throw new Error('сервер не считает счёт оплаченным');
});

// Оплаченный счёт снабженец видит оплаченным — и отменить оплату ему по-прежнему нечем.
await check('supplier-sees-the-payment-and-cannot-cancel-it', async () => {
  await openInvoice(supplier);
  supplier.screen = 'счёт: оплата';
  await supplier.page.getByText(/Оплачен \d{2}\.\d{2}\.\d{4}/).first().waitFor({ timeout: 10_000 });
  if (await supplier.page.getByRole('button', { name: 'Отменить оплату' }).count())
    throw new Error('снабженцу видна кнопка «Отменить оплату»');
  noDenials(supplier);
});

await check('accountant-no-request-was-refused', () => noDenials(accountant));

}
} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke модуля счетов под ролями снабженца и бухгалтера');
