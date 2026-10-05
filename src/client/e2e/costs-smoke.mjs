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
// ролях (ТЗ COST-28): снабженец вводит и разносит, но не платит; бухгалтер платит, но не вводит и не
// разносит.
//
// Единственный отказ, который прогон ЗОВЁТ САМ, — расклад оплаты токеном снабженца: кнопки у него нет,
// но спрятанная кнопка не доказывает, что сервер закрыт. Этот адрес назван ожидаемым и в счёт не идёт.
//
// Пишет: один счёт с уникальным номером на запуск (заводит снабженец, оплачивает бухгалтер). Счета
// копятся — проверки от их числа не зависят: счёт открывается по своему идентификатору.
//
// Требует поднятых фронта и бэка, посева (снабженец и бухгалтер — e2e/seed.mjs) и ВКЛЮЧЁННОГО модуля
// `costs` — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/costs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, registryAddress } from './harness.mjs';

const SUPPLIER_EMAIL = process.env.SMOKE_SUPPLIER_EMAIL || 'snab@bhs.local';
const ACCOUNTANT_EMAIL = process.env.SMOKE_ACCOUNTANT_EMAIL || 'buh@bhs.local';
const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
// Стройку заводит посев: у снабженца права заводить стройки нет, и не должно быть.
const SITE = 'Демо-стройка';

const browser = await launchBrowser();
const { check, summarize } = createChecks();
const number = `РОЛ-${Date.now().toString().slice(-6)}`;

/**
 * Страница одной роли: свой контекст (своя сессия) и свой счёт отказов — с адресом и экраном, на
 * котором отказ пришёл. Собираем ВСЕ, а не падаем на первом: один неверно закрытый адрес отвечает
 * отказом на каждом экране, и список «где именно» отличает общий адрес от одного экрана.
 *
 * `expected` — адреса, отказ на которых прогон вызвал сам. Набор только пополняется: событие ответа
 * может прийти ПОЗЖЕ, чем вернётся сам запрос, и «окно ожидания», закрытое сразу за ним, пропустило бы
 * собственный отказ прогона в счёт.
 */
async function seat(who) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
  const page = await context.newPage();
  page.on('pageerror', e => console.log(`  ! ошибка страницы (${who}):`, e.message));
  const seat = { page, who, screen: 'вход', denials: [], expected: new Set(), entered: false };
  page.on('response', r => {
    if (r.status() !== 403) return;
    const url = new URL(r.url());
    if (seat.expected.has(`${r.request().method()} ${url.pathname}`)) return;
    seat.denials.push(`${r.request().method()} ${url.pathname}${url.search} — экран «${seat.screen}»`);
  });
  return seat;
}

async function api(page, method, path, body) {
  const answer = await callApi(page, method, path, body);
  if (answer.status >= 400) throw new Error(`${method} ${path} → ${answer.status} ${String(answer.body).slice(0, 200)}`);
  return answer.body;
}

/** Пункты бокового меню — то, что роль видит входом в модуль. */
const menu = page => page.locator('nav').first().innerText();

/** Экран открылся и дал своим запросам дойти: отказ, оборванный следующим переходом, не посчитан. */
async function visit(seat, screen, path) {
  seat.screen = screen;
  await seat.page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
  await settled(seat.page);
}

/** Счёт — по идентификатору в адресе, а не щелчком в списке: номер прогона на стенде может повториться. */
async function openInvoice(seat, id, screen = 'счёт') {
  await visit(seat, screen, `/invoices?invoice=${id}`);
  await seat.page.getByText('Строки счёта').first().waitFor({ timeout: 10_000 });
}

/** Сумма счёта прогона в «Реестре счетов» — клетка под заголовком «Сумма», а не любая клетка строки. */
async function registryAmount(seat) {
  seat.screen = 'реестр счетов';
  await seat.page.goto('about:blank');
  await seat.page.goto(registryAddress({ type: 'condition', column: 'Номер', op: 'eq', value: number }),
    { waitUntil: 'networkidle' });
  const row = seat.page.locator('tbody tr').filter({ hasText: number }).first();
  await row.waitFor({ timeout: 10_000 });
  await settled(seat.page);

  const headers = await seat.page.locator('thead th').allInnerTexts();
  const at = headers.findIndex(h => /^Сумма(\s*\(|$)/.test(h.trim()));
  if (at < 0) throw new Error(`в реестре нет колонки «Сумма»: ${headers.join(' | ')}`);
  const cell = (await row.locator('td, th').nth(at).innerText()).replace(/[  \s]/g, '');
  if (!/^100([.,]00)?$/.test(cell))
    throw new Error(`${seat.who} не видит сумму счёта в колонке «Сумма»: «${cell}»`);
}

/** Вход — проверкой: его отказ (429, сбой формы) обязан попасть в итог, а не оборвать прогон мимо него. */
async function enter(seat, email) {
  await login(seat.page, email, PASSWORD);
  // Стартовой странице дают догрузиться: отказы считаются с входа, а оборванный переходом запрос не посчитан.
  await visit(seat, 'стартовая страница', '/document-sets');
  seat.entered = true;
}

/** Роль — не администратор и модуль ей ОТКРЫТ (а не просто включён на экземпляре). Спрашиваем сервер (AUTH-14). */
async function notAdministrator(seat) {
  seat.screen = 'проверка учётной записи';
  const access = await api(seat.page, 'GET', '/account/access');
  if ((access.permissions ?? []).includes('core.users.manage'))
    throw new Error(`${seat.who} вошёл с правами администратора — проверки ниже ничего не значат`);
  const costs = (access.modules ?? []).find(m => m.code === 'costs');
  if (!costs?.available)
    throw new Error(`модуль costs роли «${seat.who}» не открыт: ${JSON.stringify(costs ?? null)}`);
}

async function absent(seat, names) {
  for (const name of names)
    if (await seat.page.getByRole('button', { name, exact: true }).count())
      throw new Error(`роли «${seat.who}» видна кнопка «${name}» — права на это действие у роли нет`);
}

function noDenials(seat) {
  if (seat.denials.length)
    throw new Error(`запросов с отказом доступа у роли «${seat.who}»: ${seat.denials.length}\n      `
      + seat.denials.join('\n      '));
}

const supplier = await seat('снабженец');
const accountant = await seat('бухгалтер');
let invoiceId = null;
let site = null;

// ══ Снабженец: вводит и разносит ══════════════════════════════════════════════════════════════════
async function supplierPart() {
  await check('supplier-is-not-an-administrator', () => notAdministrator(supplier));

  // Счёт заводит САМ снабженец, своим токеном: подготовка администратором спрятала бы отказ на первом шаге.
  await check('supplier-enters-an-invoice', async () => {
    await visit(supplier, 'счета', '/invoices');
    await supplier.page.getByRole('button', { name: 'Новый счёт' }).waitFor({ timeout: 10_000 });

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
  if (invoiceId === null) return;

  await check('supplier-opens-the-registry', async () => {
    await registryAmount(supplier);
    const items = await menu(supplier.page);
    for (const label of ['Счета', 'Реестр счетов'])
      if (!items.includes(label)) throw new Error(`в меню снабженца нет пункта «${label}»`);
    // Отчёт по затратам и закрытие периода — бухгалтеру: пункт без права был бы дверью в отказ.
    for (const label of ['Затраты по стройке', 'Учётный период'])
      if (items.includes(label)) throw new Error(`в меню снабженца есть пункт «${label}» — права на него у роли нет`);
  });

  await check('supplier-allocates-the-invoice', async () => {
    await openInvoice(supplier, invoiceId, 'счёт: разноска на объект');
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
    await openInvoice(supplier, invoiceId, 'счёт: оплата');
    await absent(supplier, ['Отметить оплату', 'Отменить оплату']);

    // Спрятанная кнопка не доказывает, что сервер закрыт: спрашиваем его самого. Этот отказ — ожидаемый.
    const path = `/costs/invoices/${invoiceId}/paid/preview`;
    supplier.expected.add(`POST /api${path}`);
    const answer = await callApi(supplier.page, 'POST', path, { paidOn: null });
    if (answer.status !== 403)
      throw new Error(`сервер ответил снабженцу на расклад оплаты ${answer.status}, а обязан отказать (403)`);
  });

  await check('supplier-enters-and-posts-a-waybill', () => waybillPart(supplier));

  await check('supplier-no-request-was-refused', () => noDenials(supplier));
}

/** Позиция номенклатуры в строке накладной — первой из найденных: какая именно, проверке не важно. */
async function pickPosition(seat, row) {
  await row.getByRole('button', { name: 'выбрать позицию' }).click();
  const picker = seat.page.getByRole('dialog', { name: 'Позиция номенклатуры' });
  const first = picker.locator('div.overflow-y-auto button').first();
  await first.waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('в выборе позиции пусто — посев номенклатуры не отработал'); });
  await first.click();
  await picker.waitFor({ state: 'hidden', timeout: 10_000 });
}

/**
 * Накладная (D1, issue #1083): вводится, проводится, и несопоставленная строка названа числом — на
 * форме и рядом с перечнем «материалы на объекте». Экраном, а не запросами: адреса стережёт backend.
 */
async function waybillPart(seat) {
  const { page } = seat;
  const waybill = `РН-${Date.now()}`;
  await visit(seat, 'накладные', '/waybills');
  if (!(await menu(page)).includes('Накладные')) throw new Error('в меню снабженца нет пункта «Накладные»');

  seat.screen = 'ввод накладной';
  await page.getByRole('button', { name: 'Новая накладная' }).click();
  await page.getByText('Строки накладной').waitFor({ timeout: 10_000 });
  await page.getByLabel('Номер', { exact: true }).fill(waybill);
  await page.getByLabel('Дата отпуска').fill('2026-09-01');
  await page.getByLabel('Получатель — стройка').first().selectOption({ label: SITE });

  for (const [name, quantity] of [['Кабель прогона', '12,5'], ['Хомут прогона', '4']]) {
    await page.getByRole('button', { name: 'Строка', exact: true }).click();
    const row = page.locator('tbody tr').last();
    await row.getByLabel(/Наименование в накладной/).fill(name);
    await row.getByLabel(/Количество/).fill(quantity);
  }
  await pickPosition(seat, page.locator('tbody tr').first());

  // «Провести» сохраняет набранное само: несохранённая правка иначе пропала бы молча.
  await page.getByRole('button', { name: 'Провести' }).click();
  await page.getByText('Проведена', { exact: true }).waitFor({ timeout: 10_000 });
  await page.getByText(/Не сопоставлено: 1 строка\./).waitFor({ timeout: 10_000 });
  await absent(seat, ['Сохранить', 'Провести']);

  seat.screen = 'материалы на объекте';
  await page.getByRole('button', { name: 'Материалы на объекте' }).click();
  const materials = page.getByRole('dialog', { name: 'Материалы на объекте' });
  await materials.getByLabel('Стройка').selectOption({ label: SITE });
  // Оговорка и перечень — вместе: перечень без неё читался бы как «выдано только это».
  await materials.getByText(/Не сопоставлено: \d+ строк/).waitFor({ timeout: 10_000 });
  await materials.locator('tbody tr').first().waitFor({ timeout: 10_000 });
  // Несопоставленная строка в перечень не попадает. Искать её наименование из бумаги бессмысленно —
  // перечень показывает название ПОЗИЦИИ, и текста строки в нём нет ни при каком исходе (ревью
  // PR #1206). Попавшая строка — это запись без позиции: её и ищем, в ответе и на экране.
  const issued = await api(page, 'GET', `/costs/materials?constructionId=${site.id}`);
  const stray = issued.items.filter(i => !i.nomenclatureId || /^0{8}-/.test(i.nomenclatureId) || i.name === null);
  if (stray.length) throw new Error(`в перечне материалов запись без позиции номенклатуры: ${JSON.stringify(stray)}`);
  if (issued.unmatchedLines < 1) throw new Error('сервер не называет несопоставленную строку числом');
  const rows = await materials.locator('tbody tr').count();
  if (rows !== issued.items.length)
    throw new Error(`на экране строк перечня ${rows}, а позиций в ответе ${issued.items.length}`);
  if (/₽|руб/i.test(await materials.innerText())) throw new Error('в перечне материалов видны деньги');
  await page.keyboard.press('Escape');
  await materials.waitFor({ state: 'hidden', timeout: 10_000 });

  // Сопоставление — у проведённой, без возврата в черновик.
  seat.screen = 'сопоставление строки накладной';
  await pickPosition(seat, page.locator('tbody tr').last());
  await page.getByText(/Не сопоставлено:/).waitFor({ state: 'hidden', timeout: 10_000 });
  await page.getByText('Проведена', { exact: true }).waitFor({ timeout: 10_000 });
  await settled(page);
}

// ══ Бухгалтер: читает и отмечает оплату ═══════════════════════════════════════════════════════════
async function accountantPart() {
  await check('accountant-is-not-an-administrator', () => notAdministrator(accountant));

  await check('accountant-opens-his-screens', async () => {
    await registryAmount(accountant);
    const items = await menu(accountant.page);
    for (const label of ['Счета', 'Реестр счетов', 'Затраты по стройке', 'Учётный период'])
      if (!items.includes(label)) throw new Error(`в меню бухгалтера нет пункта «${label}»`);
    // Накладные — своим правом (COST-29): право на счета их не открывает.
    if (items.includes('Накладные')) throw new Error('в меню бухгалтера есть «Накладные» — права на них у роли нет');

    // Пункт в меню — обещание экрана: открываем каждый, отказы за дверью считает общий счёт.
    await visit(accountant, 'затраты по стройке', '/site-costs');
    await accountant.page.locator('main').getByRole('heading', { name: /Затраты по стройке/ }).waitFor({ timeout: 10_000 });
    await visit(accountant, 'учётный период', '/periods');
    await accountant.page.locator('main').getByRole('heading', { name: 'Учётный период' }).waitFor({ timeout: 10_000 });
  });

  await check('accountant-reads-the-invoice-and-cannot-change-it', async () => {
    await visit(accountant, 'счета', '/invoices');
    await accountant.page.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10_000 });
    await absent(accountant, ['Новый счёт']);

    await openInvoice(accountant, invoiceId, 'счёт: чтение');
    await accountant.page.getByText('Только чтение: права вводить счета нет').waitFor({ timeout: 10_000 });
    await absent(accountant, ['Сохранить', 'Добавить строку', 'Вернуть в черновик', 'Приложить скан', 'Заменить скан']);
    if (await accountant.page.getByLabel('Объект счёта', { exact: true }).count())
      throw new Error('бухгалтеру виден выбор «Объект счёта» — права на разноску у роли нет');

    // Читать разноску он обязан: по ней считаются затраты, которые он закрывает.
    await accountant.page.getByRole('button', { name: 'разноска по объектам' }).click();
    const matrix = accountant.page.getByRole('dialog', { name: 'Разноска по объектам' });
    await matrix.waitFor({ timeout: 10_000 });
    await settled(accountant.page);
    const text = await matrix.innerText();
    if (!text.includes(SITE) || !text.includes('₽'))
      throw new Error(`бухгалтер не видит разноску счёта на «${SITE}»: ${text.slice(0, 300)}`);
    await absent(accountant, ['Сохранить разноску', 'Добавить объект']);
    await accountant.page.keyboard.press('Escape');
  });

  await check('accountant-marks-payment', async () => {
    await openInvoice(accountant, invoiceId, 'счёт: оплата');
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
    await openInvoice(supplier, invoiceId, 'счёт: оплата');
    await supplier.page.getByText(/Оплачен \d{2}\.\d{2}\.\d{4}/).first().waitFor({ timeout: 10_000 });
    await absent(supplier, ['Отменить оплату']);
    noDenials(supplier);
  });

  await check('accountant-no-request-was-refused', () => noDenials(accountant));
}

try {
  await check('supplier-logs-in', () => enter(supplier, SUPPLIER_EMAIL));
  if (supplier.entered) {
    await supplier.page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    if ((await supplier.page.getByRole('heading', { name: 'Счета на оплату' }).count()) === 0) {
      console.error('Экрана счетов у снабженца нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs) '
        + 'и заведён ли снабженец посевом? Без этого проверять нечего, и зелёный прогон был бы отчётом о '
        + 'работе, которой не было.');
      process.exit(1);
    }
    await supplierPart();
  }

  // Бухгалтер идёт по счёту снабженца: без счёта его проверкам не на чем стоять — они НЕ запускаются,
  // и провал выше остаётся единственной названной причиной.
  if (invoiceId !== null) {
    await check('accountant-logs-in', () => enter(accountant, ACCOUNTANT_EMAIL));
    if (accountant.entered) await accountantPart();
  }
} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke модуля счетов под ролями снабженца и бухгалтера');
