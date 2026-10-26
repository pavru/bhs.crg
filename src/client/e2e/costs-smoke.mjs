// Живой прогон модуля «Счета и накладные» ПОД ЕГО РОЛЯМИ — снабженцем, бухгалтером и руководителем
// (N1, issue #1102).
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
// ролях (ТЗ COST-28): снабженец вводит и разносит, но не платит; бухгалтер платит, а с E2 (issue #1100)
// ещё и правит счета и разноску — без этого ему нечем исправить счёт закрытого периода (решение владельца
// 16.10.2026); руководитель читает всё и не правит ничего. Проверку «счёт только для чтения» раньше нёс
// бухгалтер — теперь её несёт руководитель.
//
// Единственный отказ, который прогон ЗОВЁТ САМ, — расклад оплаты токеном снабженца: кнопки у него нет,
// но спрятанная кнопка не доказывает, что сервер закрыт. Этот адрес назван ожидаемым и в счёт не идёт.
//
// Пишет: один счёт с уникальным номером на запуск (заводит снабженец, оплачивает бухгалтер). Счета
// копятся — проверки от их числа не зависят: счёт открывается по своему идентификатору.
//
// Требует поднятых фронта и бэка, посева (снабженец, бухгалтер, руководитель — e2e/seed.mjs) и
// ВКЛЮЧЁННОГО модуля `costs` — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/costs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, registryAddress, pickOption, menuText } from './harness.mjs';
import { extraFieldChecks } from './invoice-extra-field-checks.mjs';

const SUPPLIER_EMAIL = process.env.SMOKE_SUPPLIER_EMAIL || 'snab@bhs.local';
const ACCOUNTANT_EMAIL = process.env.SMOKE_ACCOUNTANT_EMAIL || 'buh@bhs.local';
const READER_EMAIL = process.env.SMOKE_READER_EMAIL || 'ruk@bhs.local';
const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
// Стройку заводит посев: у снабженца права заводить стройки нет, и не должно быть.
const SITE = 'Демо-стройка';

const browser = await launchBrowser();
const { check, skip, summarize } = createChecks();
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
// Меню читается на экранах «Учёта» (реестр, накладные) — их подпанель и проверяется.
const menu = page => menuText(page);

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
const reader = await seat('руководитель');
let invoiceId = null;
/** Накладная, проведённая снабженцем, — её исправляет проверка закрытого периода. */
let postedWaybillId = null;
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
    const organizations = await api(supplier.page, 'GET', '/costs/organizations?purpose=choice');
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

  // Поле, дописанное администратором в тип «Счёт», рабочая роль видит и правит (issue #1482).
  await extraFieldChecks({ browser, check, skip, seat: supplier, invoiceId, open: id => openInvoice(supplier, id, 'счёт: дописанное поле') });

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
      pickOption(select, SITE),
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
  await check('supplier-deletes-a-mistaken-waybill', () => waybillDeletePart(supplier));
  await check('supplier-sees-a-neighbour-line-choice-in-waybill', () => waybillNeighbourPart(supplier));
  await check('supplier-keeps-typed-waybill-when-reread-fails', () => waybillRereadPart(supplier));

  await check('supplier-no-request-was-refused', () => noDenials(supplier));
}

/**
 * Позиция номенклатуры в строке накладной — первой из найденных: какая именно, проверке не важно.
 *
 * ⚠️ Ищется по «посев», а не берётся из пустого запроса: с #1518 пустой запрос показывает кандидатов для
 * слов строки, а не голову алфавита, — и у «Хомута прогона» их может не быть вовсе.
 */
async function pickPosition(seat, row) {
  await row.getByRole('button', { name: 'выбрать позицию' }).click();
  const picker = seat.page.getByRole('dialog', { name: 'Позиция номенклатуры' });
  await picker.getByPlaceholder('часть наименования').fill('посев');
  const first = picker.getByRole('option').first();
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
  // Дата — сегментами в маске языка (issue #1467): нативного поля с ISO-строкой у шапки больше нет.
  const issued = page.getByRole('group', { name: 'Дата отпуска', exact: true });
  await issued.getByRole('textbox', { name: 'день', exact: true }).click();
  await page.keyboard.type('01092026');
  await pickOption(page.getByLabel('Получатель — стройка').first(), SITE);

  for (const [name, quantity] of [['Кабель прогона', '12,5'], ['Хомут прогона', '4']]) {
    await page.getByRole('button', { name: 'Добавить строку', exact: true }).click();
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
  await lockedHeader(page, waybill);
  postedWaybillId = new URL(page.url()).searchParams.get('waybill');

  seat.screen = 'материалы на объекте';
  await page.getByRole('button', { name: 'Материалы на объекте' }).click();
  const materials = page.getByRole('dialog', { name: 'Материалы на объекте' });
  await pickOption(materials.getByLabel('Стройка'), SITE);
  // Оговорка и перечень — вместе: перечень без неё читался бы как «выдано только это».
  await materials.getByText(/Не сопоставлено: \d+ строк/).waitFor({ timeout: 10_000 });
  await materials.locator('tbody tr').first().waitFor({ timeout: 10_000 });
  // Несопоставленная строка в перечень не попадает. Искать её наименование из бумаги бессмысленно —
  // перечень показывает название ПОЗИЦИИ, и текста строки в нём нет ни при каком исходе (ревью
  // PR #1206). Попавшая строка — это запись без позиции: её и ищем, в ответе и на экране.
  const given = await api(page, 'GET', `/costs/materials?constructionId=${site.id}`);
  const stray = given.items.filter(i => !i.nomenclatureId || /^0{8}-/.test(i.nomenclatureId) || i.name === null);
  if (stray.length) throw new Error(`в перечне материалов запись без позиции номенклатуры: ${JSON.stringify(stray)}`);
  if (given.unmatchedLines < 1) throw new Error('сервер не называет несопоставленную строку числом');
  const rows = await materials.locator('tbody tr').count();
  if (rows !== given.items.length)
    throw new Error(`на экране строк перечня ${rows}, а позиций в ответе ${given.items.length}`);
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

/**
 * Шапка проведённой накладной — поля для чтения, как у запертого счёта (issue #1467): значения видны,
 * а отключённое поле читалось бы «здесь ничего нет». Дата — набранная сегментами, в маске языка.
 */
async function lockedHeader(page, waybill) {
  for (const label of ['Номер', 'Склад', 'Получил', 'Примечание', 'Получатель — стройка']) {
    const field = page.getByLabel(label, { exact: true });
    if (await field.isDisabled() || await field.getAttribute('readonly') === null)
      throw new Error(`«${label}» проведённой накладной не поле для чтения`);
  }
  const segments = page.getByRole('group', { name: 'Дата отпуска', exact: true }).getByRole('textbox');
  if (!(await segments.evaluateAll(els => els.length > 0 && els.every(e => e.readOnly && !e.disabled))))
    throw new Error('дата отпуска проведённой накладной не поле для чтения');

  const number = page.getByLabel('Номер', { exact: true });
  if (await number.inputValue() !== waybill) throw new Error(`номер на шапке не тот: ${await number.inputValue()}`);
  const site = page.getByLabel('Получатель — стройка', { exact: true });
  if (await site.inputValue() !== SITE) throw new Error(`стройка на запертой шапке: «${await site.inputValue()}»`);
  // Дату сверяет сервер, а не склейка сегментов: в чужой маске (ММ.ДД) те же цифры — другая дата.
  const id = new URL(page.url()).searchParams.get('waybill');
  const saved = await api(page, 'GET', `/costs/waybills/${id}`);
  if (saved.issuedOn !== '2026-09-01') throw new Error(`дата отпуска записана как ${saved.issuedOn}`);
  if (await page.locator('input[type="date"]').count()) throw new Error('на форме накладной нативное поле даты');
}

/**
 * Удаление накладной (issue #1283): завёл — удалил — в списке нет. У проведённой действия нет вовсе.
 * Идёт сразу за вводом: на экране открыта только что проведённая накладная.
 */
async function waybillDeletePart(seat) {
  const { page } = seat;
  seat.screen = 'удаление накладной';
  // Невыполнимое действие скрыто, а не показано с отказом: у проведённой меню «Ещё» нет.
  if (await page.getByRole('button', { name: 'Ещё', exact: true }).count())
    throw new Error('у проведённой накладной есть меню «Ещё» — а удалить её нельзя');

  const waybill = `РН-ОШИБКА-${Date.now()}`;
  await page.getByRole('button', { name: 'Новая накладная' }).click();
  await page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 });
  await page.getByLabel('Номер', { exact: true }).fill(waybill);
  await page.getByRole('button', { name: 'Добавить строку', exact: true }).click();
  await page.locator('tbody tr').last().getByLabel(/Наименование в накладной/).fill('Заведено по ошибке');

  // Недописанная дата не записывается молча (ревью PR #1556): наружу она не уходит, и сохранение без
  // отказа записало бы прежнюю — здесь пустую — при «Дата не дописана» на экране.
  seat.screen = 'недописанная дата накладной';
  const date = page.getByRole('group', { name: 'Дата отпуска', exact: true });
  await date.getByRole('textbox', { name: 'день', exact: true }).click();
  await page.keyboard.type('0109');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByText(/Накладная не записана: в дате отпуска ошибка/).first().waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('накладная с недописанной датой сохранена без отказа'); });
  // Заголовок называет записанный номер: набранный, но не записанный, в нём не появляется.
  if (await page.getByRole('heading', { name: new RegExp(waybill) }).count())
    throw new Error('накладная с недописанной датой записана');
  await date.getByRole('textbox', { name: 'год', exact: true }).click();
  await page.keyboard.type('2026');

  seat.screen = 'удаление накладной';
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByRole('heading', { name: new RegExp(waybill) }).waitFor({ timeout: 10_000 });
  const id = new URL(page.url()).searchParams.get('waybill');

  await page.getByRole('button', { name: 'Ещё', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Удалить накладную…' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByText('Корзины нет').waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('диалог не сказал, что вернуть накладную будет нельзя'); });
  await dialog.getByText('строки накладной — 1').waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('диалог не назвал, сколько строк уйдёт'); });
  await Promise.all([
    page.waitForResponse(r => r.request().method() === 'DELETE' && r.url().endsWith(`/costs/waybills/${id}`) && r.status() === 204),
    dialog.getByRole('button', { name: `Удалить накладную № ${waybill}` }).click(),
  ]);
  await page.getByText('Выберите накладную').waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('после удаления форма не закрылась'); });
  if (page.url().includes('waybill=')) throw new Error(`удалённая накладная осталась в адресе: ${page.url()}`);
  if ((await api(page, 'GET', '/costs/waybills')).items.some(i => i.id === id))
    throw new Error('удалённая накладная осталась в списке сервера');
  await page.getByRole('button', { name: new RegExp(waybill) }).waitFor({ state: 'detached', timeout: 10_000 })
    .catch(() => { throw new Error('список слева всё ещё показывает удалённую накладную'); });

  // Сосед: накладная открыта, в поле набрано — и тут её удаляют. Узнаёт он об этом на сохранении.
  const neighbour = `РН-СОСЕД-${Date.now()}`;
  await page.getByRole('button', { name: 'Новая накладная' }).click();
  await page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 });
  await page.getByLabel('Номер', { exact: true }).fill(neighbour);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByRole('heading', { name: new RegExp(neighbour) }).waitFor({ timeout: 10_000 });
  const other = new URL(page.url()).searchParams.get('waybill');
  const seen = (await api(page, 'GET', `/costs/waybills/${other}`)).version;
  await page.getByLabel('Примечание').fill('набрано до удаления');
  const removed = await page.evaluate(async ([wid, version]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    return (await fetch(`/api/costs/waybills/${wid}`, {
      method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': version },
    })).status;
  }, [other, seen]);
  if (removed !== 204) throw new Error(`накладная не удалена запросом соседа: ${removed}`);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByTestId('waybill-gone-open').waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('форма не сказала, что накладной больше нет'); });
  if (await page.getByLabel('Примечание').inputValue() !== 'набрано до удаления')
    throw new Error('набранное пропало вместе с накладной — а полоса обещает, что оно на экране');
  if (await page.getByRole('button', { name: 'Сохранить', exact: true }).count())
    throw new Error('у накладной, которой нет, осталась кнопка «Сохранить»');
  // Уход щелчком по другой строке, а не кнопкой: удалённая обязана уйти из списка и так.
  await page.getByRole('button', { name: /РН-\d+/ }).first().click();
  await page.getByRole('button', { name: new RegExp(neighbour) }).waitFor({ state: 'detached', timeout: 10_000 })
    .catch(() => { throw new Error('удалённая соседом накладная осталась в списке после ухода с неё'); });
  await settled(page);
}

/**
 * Неудачное перечитывание открытой накладной (issue #1287): форма остаётся с набранным, отказ — полосой
 * с «Повторить». Сохранение отвечает «изменили», форма перечитывает накладную, а чтение падает.
 */
/**
 * Соседняя строка накладной (issue #1518): позиция, выбранная строке с тем же наименованием, предложена
 * следующей — «выбрана в строке 1». Артикула у накладной нет, сосед узнаётся по наименованию, с тем же
 * приведением регистра и пробелов, что ключ соответствия на сервере.
 */
async function waybillNeighbourPart(seat) {
  const { page } = seat;
  seat.screen = 'соседняя строка накладной';
  const waybill = `РН-СТРОКИ-${Date.now()}`;
  await page.getByRole('button', { name: 'Новая накладная' }).click();
  await page.getByText('Строки накладной').waitFor({ timeout: 10_000 });
  await page.getByLabel('Номер', { exact: true }).fill(waybill);
  for (const name of ['Кабель соседа', 'кабель  СОСЕДА']) {
    await page.getByRole('button', { name: 'Добавить строку', exact: true }).click();
    const row = page.locator('tbody tr').last();
    await row.getByLabel(/Наименование в накладной/).fill(name);
    await row.getByLabel(/Количество/).fill('1');
  }
  await pickPosition(seat, page.locator('tbody tr').first());

  await page.locator('tbody tr').last().getByRole('button', { name: 'выбрать позицию' }).click();
  const picker = page.getByRole('dialog', { name: 'Позиция номенклатуры' });
  await picker.getByRole('option', { name: /выбрана в строке 1/ }).waitFor({ timeout: 15_000 })
    .catch(() => { throw new Error('позиция строки 1 с тем же наименованием не предложена строке 2'); });
  await page.keyboard.press('Escape');
  await picker.waitFor({ state: 'hidden', timeout: 10_000 });

  // Черновик сохраняется: следующая проверка заводит новую накладную, и несохранённое её бы остановило.
  // ⚠️ И экран возвращается к проведённой: следующая ждёт «Черновик» как знак новой формы, а при открытом
  // черновике он уже на экране — и номер набирался бы в старую форму.
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByRole('heading', { name: new RegExp(waybill) }).waitFor({ timeout: 10_000 });
  await page.getByRole('button', { name: /РН-\d+/ }).first().click();
  await page.getByText('Проведена', { exact: true }).waitFor({ timeout: 10_000 });
  await settled(page);
}

async function waybillRereadPart(seat) {
  const { page } = seat;
  seat.screen = 'перечитывание накладной';
  const waybill = `РН-ОБРЫВ-${Date.now()}`;
  await page.getByRole('button', { name: 'Новая накладная' }).click();
  await page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 });
  await page.getByLabel('Номер', { exact: true }).fill(waybill);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByRole('heading', { name: new RegExp(waybill) }).waitFor({ timeout: 10_000 });
  const id = new URL(page.url()).searchParams.get('waybill');
  await page.getByLabel('Примечание').fill('набрано до обрыва');

  const address = `**/api/costs/waybills/${id}`;
  await page.route(address, route => route.request().method() === 'GET'
    ? route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"Сервер недоступен (проба)."}' })
    : route.fulfill({ status: 409, contentType: 'application/json', body: '{"error":"Накладную тем временем изменили (проба)."}' }));
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await page.getByTestId('reread-failed').waitFor({ timeout: 15_000 })
    .catch(() => { throw new Error('полосы «не перечиталась» нет — форма снята или отказ не показан'); });
  if (await page.getByLabel('Примечание').inputValue() !== 'набрано до обрыва')
    throw new Error('набранное пропало при неудачном перечитывании');

  await page.unroute(address);
  await page.getByTestId('reread-failed').getByRole('button', { name: 'Повторить' }).click();
  await page.getByTestId('reread-failed').waitFor({ state: 'detached', timeout: 10_000 })
    .catch(() => { throw new Error('после удачного повтора полоса осталась'); });
  if (await page.getByLabel('Примечание').inputValue() !== 'набрано до обрыва')
    throw new Error('набранное пропало после удачного повтора');
  await settled(page);
}

// ══ Руководитель: читает и не правит ══════════════════════════════════════════════════════════════
async function readerPart() {
  await check('reader-is-not-an-administrator', () => notAdministrator(reader));

  await check('reader-reads-the-invoice-and-cannot-change-it', async () => {
    await visit(reader, 'счета', '/invoices');
    await reader.page.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10_000 });
    await absent(reader, ['Новый счёт']);

    await openInvoice(reader, invoiceId, 'счёт: чтение');
    await reader.page.getByText('Только чтение: права вводить счета нет').waitFor({ timeout: 10_000 });
    await absent(reader, ['Сохранить', 'Добавить строку', 'Вернуть в черновик', 'Приложить файл', 'Заменить файл']);
    if (await reader.page.getByLabel('Объект счёта', { exact: true }).count())
      throw new Error('руководителю виден выбор «Объект счёта» — права на разноску у роли нет');

    // Читать разноску он обязан: по ней считаются затраты, которые он видит в сводках.
    await reader.page.getByRole('button', { name: 'разноска по объектам' }).click();
    const matrix = reader.page.getByRole('dialog', { name: 'Разноска по объектам' });
    await matrix.waitFor({ timeout: 10_000 });
    await settled(reader.page);
    const text = await matrix.innerText();
    if (!text.includes(SITE) || !text.includes('₽'))
      throw new Error(`руководитель не видит разноску счёта на «${SITE}»: ${text.slice(0, 300)}`);
    await absent(reader, ['Сохранить разноску', 'Добавить объект']);
    await reader.page.keyboard.press('Escape');
  });

  await check('reader-no-request-was-refused', () => noDenials(reader));
}

/**
 * Исправление накладной закрытого периода (issue #1340, макет 1a): бухгалтер закрывает стройке день, в
 * который легла накладная снабженца, — снабженец исправляет количество с причиной. Накладная остаётся
 * проведённой, исправление видно в истории и строкой в списке. Закрытие отменяется в конце: по этой
 * стройке идут и другие прогоны.
 */
async function waybillCorrectionPart() {
  if (!postedWaybillId) throw new Error('проведённой накладной снабженца нет — исправлять нечего');
  const contour = { contour: 'construction', constructionId: site.id };
  const own = async () => (await api(accountant.page, 'GET', '/periods')).constructions.find(c => c.constructionId === site.id);
  const reason = `Прогон: пересчёт по факту ${Date.now()}`;

  accountant.screen = 'закрытие дня накладной';
  const before = await own();
  const through = (await api(supplier.page, 'GET', `/costs/waybills/${postedWaybillId}`)).accountingOn;
  const from = before.expectedFrom ?? through;
  const preview = await api(accountant.page, 'POST', '/periods/close/preview', { ...contour, from, through });
  await api(accountant.page, 'POST', '/periods/close',
    { ...contour, from, through, ifMatch: before.closedThrough ?? 'none', reason: 'Прогон: исправление накладной', report: preview.stamp });

  try {
    const { page } = supplier;
    supplier.screen = 'исправление накладной';
    await page.goto(`${BASE}/waybills?waybill=${postedWaybillId}`, { waitUntil: 'networkidle' });
    await page.getByTestId('waybill-locked').waitFor({ timeout: 10_000 });
    await page.getByRole('button', { name: 'Исправить', exact: true }).click();

    await page.locator('tbody tr').first().getByLabel(/Количество/).fill('11');
    const write = page.getByRole('button', { name: 'Записать исправление' });
    if (await write.isEnabled()) throw new Error('«Записать исправление» нажимается без причины');
    await page.getByLabel('Причина исправления').fill(reason);
    await Promise.all([
      page.waitForResponse(r => r.url().endsWith(`/costs/waybills/${postedWaybillId}/correction`)
        && r.request().method() === 'POST' && r.ok(), { timeout: 10_000 }),
      write.click(),
    ]);

    // История — событием: причина и что изменилось; накладная осталась проведённой и запертой.
    const history = page.getByTestId('waybill-history');
    await history.getByText(reason).waitFor({ timeout: 10_000 });
    await history.getByText(/количество 12,5 → 11/).waitFor({ timeout: 10_000 });
    await page.getByTestId('waybill-locked').waitFor({ timeout: 10_000 });
    const view = await api(page, 'GET', `/costs/waybills/${postedWaybillId}`);
    if (view.state !== 'Posted' || view.currentVersion !== 2)
      throw new Error(`после исправления накладная ${view.state}, версия ${view.currentVersion}`);

    // Пометка в списке — видимой строкой с причиной.
    await page.getByTestId('waybill-corrected').filter({ hasText: reason }).first().waitFor({ timeout: 10_000 });
  } finally {
    await api(accountant.page, 'POST', '/periods/reopen',
      { ...contour, ifMatch: through, reason: 'Прогон: исправление накладной' });
  }
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

  // Бухгалтер с E2 (issue #1100) правит счёт и разноску: исправление закрытого периода пишет и то и
  // другое. Здесь — что форма ему ОТКРЫТА; отказы на её запросах ловит общий счёт ниже.
  await check('supplier-corrects-a-locked-waybill', () => waybillCorrectionPart());

  await check('accountant-may-change-the-invoice', async () => {
    await visit(accountant, 'счета', '/invoices');
    await accountant.page.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10_000 });
    if ((await accountant.page.getByRole('button', { name: 'Новый счёт', exact: true }).count()) === 0)
      throw new Error('у бухгалтера нет «Нового счёта» — право правки счетов у роли есть');

    await openInvoice(accountant, invoiceId, 'счёт: правка');
    if (await accountant.page.getByText('Только чтение: права вводить счета нет').count())
      throw new Error('форма счёта называет себя «Только чтение» — право правки счетов у бухгалтера есть');
    await accountant.page.getByLabel('Объект счёта', { exact: true }).first().waitFor({ timeout: 10_000 });
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
    // Руководитель читает счёт ДО оплаты — таким, каким его оставил снабженец.
    await check('reader-logs-in', () => enter(reader, READER_EMAIL));
    if (reader.entered) await readerPart();

    await check('accountant-logs-in', () => enter(accountant, ACCOUNTANT_EMAIL));
    if (accountant.entered) await accountantPart();
  }
} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke модуля счетов под ролями снабженца, бухгалтера и руководителя');
