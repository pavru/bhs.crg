// Живой прогон ОПЛАТЫ СЧЁТА (задача C5 этапа 2, issue #1082, ТЗ COST-4, COST-9, COST-16).
//
// ЗАЧЕМ ОН ЕСТЬ. Расклад оплаты считает сервер, и сервер проверен своими тестами. Но утверждения задачи —
// про ЭКРАН: человек обязан увидеть перенос ДО сохранения и причину отказа — словами сервера.
//   1. `refusal-is-shown-instead-of-the-form` — счёт, у которого строки не бьются с суммой: диалог
//      называет расхождение числом сервера, полей и кнопки оплаты в нём нет.
//   1б. `payment-date-calendar-not-clipped` — календарь «Даты платежа» не срезан краем окна: последний
//       день виден, щелчок по нему ставит дату в поле (issue #1521). `…-fits-phone` — то же на 375×812;
//       сторожем обрезки он не служит: окно во весь экран не резало и раньше. `…-tab-goes-to-next-field`
//       — Tab с края календаря ведёт к следующему полю окна, а не прочь со страницы.
//   2. `preview-matches-recorded` — стройка закрыта, платёж задним числом: диалог называет перенос и
//      меняет подпись кнопки; после записи расклад счёта показывает ТЕ ЖЕ учётные даты.
//   2а. `registry-names-the-accounting-month` — реестр показывает тот же учётный месяц и деньги в нём.
//   2б. `registry-row-opens-the-allocation` — строка реестра раскрывается блоком «Разноска»; названное
//       отбором помечено, и «В отборе» равно клетке «Сумма».
//   1а. `closing-dialog-lists-what-it-records` — период закрывается ДИАЛОГОМ (E1b, issue #1099): он
//       называет незавершённое и то, что войдёт в период, повторяет незавершённое у кнопки и в
//       сообщении об успехе, после успеха не получает отказов, а «История» показывает тот же перечень.
//   3. `locked-invoice-says-why` — оплаченный счёт, попавший в закрытый период: полоса с причиной,
//      кнопок сохранения и отмены оплаты нет вовсе (а не «есть и получают 409»), и ни один запрос
//      экрана отказа не получил.
//   4. `admin-corrects-locked-invoice` — исправление запертого счёта (E2, issue #1100, PR 6b-1): без
//      причины проверка не нажимается, у строк нет своей кнопки записи, предпросмотр называет день
//      исправления, запись проходит без отказов, и сервер хранит ровно набранное — в шапке и в строке.
//      После записи секция «Исправления» (PR 6b-2) показывает исправление с версией и причиной.
//      ⚠️ Под АДМИНИСТРАТОРОМ прогона, а не под бухгалтером: входы всех наборов CI уже упираются в предел
//      частоты (`suites.test.mjs`), и второй вход здесь сделал бы красным соседний набор. Кто видит кнопку
//      («Бухгалтер» — да, «Руководитель» — нет), проверяет `mayCorrect` модульно.
//
// ⚠️ ЗАКРЫТИЕ ПЕРИОДА ЗДЕСЬ — ТОЛЬКО СВОЕЙ СТРОЙКИ, И ОНО ОТМЕНЯЕТСЯ. База прогонов общая на все
// наборы, и закрытие компании осталось бы соседям. Стройка прогона своя (заводится один раз), закрытие
// отменяется в конце — в том числе при провале; в истории закрытий остаются две записи на запуск.
//
// Требует поднятых фронта и бэка, посева и ВКЛЮЧЁННОГО модуля `costs` — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/invoice-payment-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled } from './harness.mjs';


const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);

/** Запрос к приложению ТЕМ ЖЕ токеном, что у открытой страницы. */
async function api(method, path, body) {
  return page.evaluate(async ([method, path, body]) => {
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

const shift = (iso, days) => {
  const date = new Date(`${iso}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
};
const ru = iso => iso.split('-').reverse().join('.');

let site = null;
let closedThrough = null;

try {
await login(page);
await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });

if ((await page.getByRole('heading', { name: 'Счета на оплату' }).count()) === 0) {
  console.error('Экрана счетов нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs)? '
    + 'Без него проверять нечего, и зелёный прогон был бы отчётом о работе, которой не было.');
  process.exit(1);
}

const SITE = 'Объект прогона оплаты';
site = (await api('GET', '/constructions')).find(c => c.name === SITE)
  ?? await api('POST', '/constructions', { name: SITE });

const organizations = await api('GET', '/costs/organizations?purpose=choice');
if (organizations.length === 0) {
  console.error('В справочнике нет ни одной организации — посев не отработал. Проверять нечего.');
  process.exit(1);
}

/** Счёт из одной строки-услуги, разнесённой на стройку прогона целиком. */
async function invoice(number, amount, total) {
  const created = await api('POST', '/costs/invoices', {
    requisites: {
      'Номер': number, 'Дата': '2026-09-01', 'Итого': total,
      'Поставщик': { $ref: 'catalog', entryId: organizations[0].id },
    },
  });
  const view = await api('PUT', `/costs/invoices/${created.id}/lines`, {
    lines: [{ supplierText: 'Услуга прогона', amount }],
  });
  await api('PUT', `/costs/invoices/${created.id}/lines/${view.lines[0].id}/allocation`, {
    parts: [{ construction: site.id, amount }],
  });
  return created.id;
}

async function open(number) {
  await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: new RegExp(number) }).first().click();
  await settled(page);
}

const today = (await api('GET', '/periods')).today;
const paidOn = shift(today, -10);
const through = shift(today, -5);

// Граница стройки обязана быть пустой: прошлый запуск закрытие отменил. Осталась — он упал посреди
// работы, и молча закрывать «следующий период» значило бы копить закрытые дни от запуска к запуску.
const own = (await api('GET', '/periods')).constructions.find(c => c.constructionId === site.id);
if (own?.ownClosedThrough) {
  console.error(`У стройки «${SITE}» осталось закрытие по ${own.ownClosedThrough} от прошлого запуска. `
    + 'Отмените его на странице «Учётный период» и повторите.');
  process.exit(1);
}

// ── 1. Отказ — словами сервера и вместо формы ─────────────────────────────────────────────────────
const broken = `ОПЛ-Н-${stamp}`;
await invoice(broken, 100, 150);

await check('refusal-is-shown-instead-of-the-form', async () => {
  await open(broken);
  const note = await page.locator('main').innerText();
  if (!/оплатить нельзя: сумма строк .* расходится/.test(note))
    throw new Error('рядом с кнопкой оплаты причина отказа не названа');

  await page.getByRole('button', { name: 'Отметить оплату' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Понятно' }).waitFor({ timeout: 10_000 });
  const text = await dialog.innerText();
  if (!/Оплатить нельзя: сумма строк 100[.,]00 расходится с суммой к оплате 150[.,]00 на 50[.,]00/.test(text))
    throw new Error(`отказ не назвал расхождение числом сервера: ${text.slice(0, 300)}`);
  if (await dialog.getByText('Дата платежа').count())
    throw new Error('в диалоге отказа есть поле даты — заполнять его незачем');
  await dialog.getByRole('button', { name: 'Понятно' }).click();
});

// ── 1б. Календарь даты — слой над окном: край окна его не режет, щелчок и Tab ведут по форме ────────
const calendarNumber = `ОПЛ-К-${stamp}`;
await invoice(calendarNumber, 100, 100);

/**
 * Рамка элемента календаря, когда календарь встал на место. До первого замера Radix держит обёртку
 * поппера за экраном (`translate(0, -200%)`), и Playwright считает её видимой: рамка, снятая сразу
 * после `waitFor`, на медленной машине указывала бы выше экрана (ревью PR #1526). Ждём, пока рамка
 * окажется на экране и два замера подряд совпадут.
 */
async function placedBox(locator) {
  let prev = null;
  for (let i = 0; i < 50; i++) {
    const box = await locator.boundingBox();
    if (box && box.y >= 0 && prev && box.x === prev.x && box.y === prev.y) return box;
    prev = box;
    await page.waitForTimeout(50);
  }
  throw new Error(`календарь не встал на место за 2,5 с: последняя рамка ${JSON.stringify(prev)}`);
}

/** Окно оплаты счёта прогона и в нём — открытый календарь «Даты платежа». */
async function openPaymentCalendar() {
  await open(calendarNumber);
  await page.getByRole('button', { name: 'Отметить оплату' }).click();
  const dialog = page.getByRole('dialog').first();
  await dialog.getByText(/Вся сумма войдёт в затраты/).waitFor({ timeout: 10_000 });
  await dialog.getByRole('button', { name: 'Открыть календарь' }).click();
  const grid = page.locator('[data-radix-popper-content-wrapper]').getByRole('grid', { name: 'Календарь' });
  await grid.waitFor({ timeout: 5_000 });
  return { dialog, grid };
}

/**
 * Выход из окна без записи. Если дату сменили, окно спросит «Закрыть без сохранения?» — кроме дня,
 * когда выбранный день и есть сегодняшний.
 */
async function leavePayment(dialog) {
  await dialog.getByRole('button', { name: 'Отмена' }).click();
  const question = dialog.getByText('Закрыть без сохранения?');
  await question.waitFor({ timeout: 2_000 }).then(
    () => question.locator('xpath=..').getByRole('button', { name: 'Закрыть', exact: true }).click(),
    () => {});
  await dialog.waitFor({ state: 'hidden', timeout: 5_000 });
}

/**
 * Окно оплаты невысокое, а календарь выпадает под первым полем — его низ всегда за краем окна. Пока
 * календарь портировался ВНУТРЬ окна, `overflow-hidden` окна срезал последние недели (issue #1521).
 *
 * <p>Проверяем точкой, а не рамкой: `boundingBox` у обрезанной кнопки честный, её просто не видно.
 * `elementFromPoint` в центре последнего дня обязан попасть в сам календарь, а не в подвал окна.
 * Потом по этому дню щёлкаем: выбор доходит до поля — то, что когда-то ломал портал в body (#338).</p>
 */
async function lastDayIsReachable() {
  const { dialog, grid } = await openPaymentCalendar();
  const last = grid.getByRole('gridcell').locator('button').last();
  const box = await placedBox(last);
  const covered = await page.evaluate(([x, y]) => {
    const hit = document.elementFromPoint(x, y);
    return hit?.closest('[data-radix-popper-content-wrapper]') ? null : (hit?.outerHTML.slice(0, 160) ?? 'за экраном');
  }, [box.x + box.width / 2, box.y + box.height / 2]);
  if (covered) throw new Error(`последний день календаря не виден — в его точке: ${covered}`);

  const day = (await last.innerText()).trim().padStart(2, '0');
  await last.click();
  await grid.waitFor({ state: 'hidden', timeout: 5_000 });
  const shown = await dialog.getByPlaceholder('ДД').inputValue();
  if (shown !== day) throw new Error(`щелчок по ${day} не дошёл до поля: в нём «${shown}»`);
  if (!(await dialog.isVisible())) throw new Error('щелчок по дню закрыл окно оплаты');
  await leavePayment(dialog);
}

await check('payment-date-calendar-not-clipped', lastDayIsReachable);
// Телефон: окно там во весь экран и совпадает с экраном — обрезки не было и до #1521, так что это не
// сторож обрезки. Проверяется другое: календарь помещается в 375 px (сдвиг `collisionPadding`), и
// щелчок по дню ставит дату.
await check('payment-date-calendar-fits-phone', async () => {
  await page.setViewportSize({ width: 375, height: 812 });
  try { await lastDayIsReachable(); } finally { await page.setViewportSize({ width: 1500, height: 1000 }); }
});

// Календарь лежит в конце body, вне порядка Tab формы: без своей обработки Tab с его последней
// остановки возвращался на первую кнопку календаря — по кругу, и к следующему полю вёл только Esc
// (ревью PR #1526). Tab обязан закрыть календарь и встать на следующее поле — «Платёжный документ».
await check('payment-date-calendar-tab-goes-to-next-field', async () => {
  const { dialog, grid } = await openPaymentCalendar();
  await placedBox(grid);
  for (let i = 0; i < 6 && await grid.isVisible(); i++) await page.keyboard.press('Tab');
  if (await grid.isVisible()) throw new Error('шесть нажатий Tab не вывели фокус из календаря');
  // Фокус ждём, а не снимаем мгновенно: закрытие слоя — несколько кадров, и мгновенный снимок на
  // медленной машине CI видел промежуточное состояние (body).
  const next = await dialog.getByLabel('Платёжный документ').elementHandle();
  const onNext = await page.waitForFunction(el => el === document.activeElement, next, { timeout: 3_000 })
    .then(() => true, () => false);
  if (!onNext) {
    const at = await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 160) ?? 'нигде');
    throw new Error(`Tab из календаря не дошёл до «Платёжного документа» — фокус: ${at}`);
  }
  await leavePayment(dialog);
});

// ── Данные: один счёт оплачен ДО закрытия (его запрёт), второй оплатим ПОСЛЕ (его перенесёт) ───────
const lockedNumber = `ОПЛ-З-${stamp}`;
const lockedId = await invoice(lockedNumber, 200, 200);
const seen = await api('POST', `/costs/invoices/${lockedId}/paid/preview`, { paidOn });
await api('POST', `/costs/invoices/${lockedId}/paid`, { paidOn, document: 'п/п прогона', seen: seen.stamp });

const movedNumber = `ОПЛ-П-${stamp}`;
await invoice(movedNumber, 300, 300);

// ── 1а. Период закрывают ДИАЛОГОМ: он называет, что войдёт и что не завершено, и это же ложится в запись ──
await check('closing-dialog-lists-what-it-records', async () => {
  await page.goto(`${BASE}/periods`, { waitUntil: 'networkidle' });
  await settled(page);
  // Первая строка со стройкой — в таблице контуров; в «Истории» она ниже.
  await page.locator('tbody tr').filter({ hasText: SITE }).first().getByRole('button', { name: 'Закрыть период' }).click();
  const dialog = page.getByRole('dialog');

  // «По» — второе поле дат диалога; первое («С») у первого закрытия стройки правится тоже.
  const [year, month, day] = through.split('-');
  // Ответ — на запрос именно с этой датой: перечень за промежуточные даты (после дня и месяца) не годится.
  // Ждём его с ПЕРВОГО поля: дата бывает готова раньше, чем введён год (см. проверку оплаты ниже).
  const listed = page.waitForResponse(r => r.url().includes('/periods/close/preview') && r.ok()
    && (r.request().postData() ?? '').includes(`"through":"${through}"`));
  await dialog.getByPlaceholder('ДД').nth(1).fill(day);
  await dialog.getByPlaceholder('ММ').nth(1).fill(month);
  await dialog.getByPlaceholder('ГГГГ').nth(1).fill(year);
  await dialog.getByPlaceholder('ГГГГ').nth(1).blur();
  await listed;
  await dialog.getByText('Счета и накладные').waitFor({ timeout: 10_000 });
  await settled(page);

  const text = (await dialog.innerText()).replace(/[  ]/g, ' ');
  if (process.env.SHOT_CLOSING) await page.screenshot({ path: process.env.SHOT_CLOSING });
  // Счёт прогона оплачен в закрываемые дни и не разобран: он и «не завершён», и «войдёт в период».
  const unsettled = text.match(/Оплачены в периоде, но не разобраны: (\d[^\n]*)/)?.[1]?.trim();
  if (!unsettled) throw new Error(`диалог не назвал незавершённое: ${text.slice(0, 500)}`);
  if (!/Оплаченные счета, вошедшие в период: \d/.test(text)) throw new Error(`диалог не назвал, что войдёт в период: ${text.slice(0, 500)}`);
  if (!/по учётному периоду оплаты/.test(text)) throw new Error('диалог не назвал правило даты');
  // То же число — рядом с кнопкой: перечень длинный, и кнопка бывает видна без него.
  const counted = unsettled.split(' на ')[0];
  if (!text.includes(`Не завершено: ${counted}`)) throw new Error(`в футере нет «Не завершено: ${counted}»: ${text.slice(-200)}`);

  const button = dialog.getByRole('button', { name: 'Закрыть период' });
  if (await button.isDisabled()) throw new Error('перечень показан, а кнопка закрытия недоступна');
  const refused = [];
  // Только запросы периода: посторонний отказ (фоновый опрос, 429 стенда) диалога не касается.
  const listen = response => {
    if (response.status() >= 400 && response.url().includes('/api/periods')) refused.push(`${response.status()} ${response.url()}`);
  };
  // После отправки закрытия перечень не запрашивается ВОВСЕ — ни с отказом, ни без: граница сдвинулась,
  // и запрос за прежние дни получил бы «уже закрыт», а за новые — период «наоборот». Ловим сам запрос,
  // а не только отказ: уходил ли он, зависело от скорости машины, и на стенде отказа не было.
  // Счёт идёт с самой отправки, а не с подписки: до неё перечень вправе перечитаться — по фокусу окна,
  // например, — и это законный запрос с верными датами.
  const asked = [];
  let sent = false;
  const ask = request => {
    if (request.url().endsWith('/periods/close')) sent = true;
    else if (sent && request.url().includes('/periods/close/preview')) asked.push(request.postData() ?? '');
  };
  page.on('response', listen);
  page.on('request', ask);
  try {
    await Promise.all([
      page.waitForResponse(r => r.url().endsWith('/periods/close') && r.request().method() === 'POST' && r.ok()),
      button.click(),
    ]);
    closedThrough = through;
    await page.getByText(new RegExp(`период закрыт по ${ru(through).replace(/\./g, '\\.')}\\. Не завершено: ${counted}`)).first()
      .waitFor({ timeout: 10_000 });
    await dialog.waitFor({ state: 'hidden', timeout: 10_000 });
    await settled(page);
  } finally {
    page.off('response', listen);
    page.off('request', ask);
  }
  // После удавшегося закрытия перечень за закрытые дни не перезапрашивается — иначе сервер ответил бы
  // «уже закрыт», и поверх успеха мелькнула бы ошибка.
  if (asked.length) throw new Error(`после отправки закрытия диалог запросил перечень: ${asked.join(' | ')}`);
  if (refused.length) throw new Error(`закрытие удалось, а экран получил отказы: ${refused.join('; ')}`);

  // «История» показывает то, что показал диалог, — из записи о закрытии.
  const record = page.locator('tbody tr').filter({ hasText: 'Закрыт период' }).filter({ hasText: SITE }).first();
  await record.getByRole('button', { name: 'Что показал диалог при закрытии' }).click();
  await record.getByText('Так диалог выглядел в день закрытия', { exact: false }).waitFor({ timeout: 10_000 });
  const recorded = (await record.innerText()).replace(/[  ]/g, ' ');
  if (!recorded.includes(`Оплачены в периоде, но не разобраны: ${unsettled}`))
    throw new Error(`в «Истории» не то, что показал диалог («${unsettled}»): ${recorded.slice(0, 500)}`);
});
// Остальные проверки стоят на закрытом периоде: без него они провалились бы все и заслонили бы причину.
// Итог подводится в одном месте — в конце файла (сторож `suites.test.mjs`).
if (closedThrough) {

// ── 2. Перенос назван до сохранения, и записанное равно показанному ────────────────────────────────
await check('preview-matches-recorded', async () => {
  await open(movedNumber);
  await page.getByRole('button', { name: 'Отметить оплату' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByText(/Вся сумма войдёт в затраты/).waitFor({ timeout: 10_000 });

  // Дата — задним числом, в закрытый для стройки день.
  const [year, month, day] = paidOn.split('-');
  // Ответ ждём С ПЕРВОГО поля и именно на эту дату. Раньше ждали его только с вводом года — а год и
  // месяц у даты «десять дней назад» с одиннадцатого числа те же, что стоят в поле: дата готова уже
  // после дня, запрос уходит тогда же, и на вводе года ждать нечего. Прогон краснел таймаутом с 11-го
  // числа каждого месяца и до его конца.
  const previewed = page.waitForResponse(r => r.url().includes('/paid/preview') && r.ok()
    && (r.request().postData() ?? '').includes(`"paidOn":"${paidOn}"`));
  await dialog.getByPlaceholder('ДД').fill(day);
  await dialog.getByPlaceholder('ММ').fill(month);
  await dialog.getByPlaceholder('ГГГГ').fill(year);
  await dialog.getByPlaceholder('ГГГГ').blur();
  await previewed;

  const button = dialog.getByRole('button', { name: 'Отметить оплату с переносом' });
  await button.waitFor({ timeout: 10_000 });
  const shown = await dialog.innerText();
  const accounting = ru(shift(through, 1));
  if (!/Переносится: 1 стройка, 300,00/.test(shown)) throw new Error(`перенос не назван: ${shown.slice(0, 400)}`);
  if (!shown.includes(`закрыто по ${ru(through)}`) || !shown.includes(accounting))
    throw new Error(`причина и учётная дата переноса не названы: ${shown.slice(0, 400)}`);

  await Promise.all([
    page.waitForResponse(r => r.url().endsWith('/paid') && r.request().method() === 'POST' && r.ok()),
    button.click(),
  ]);
  await page.getByText(`Оплачен ${ru(paidOn)}`).first().waitFor({ timeout: 10_000 });

  // Записанное — тем же раскладом: учётная дата та, что была обещана.
  await page.getByRole('button', { name: 'расклад' }).click();
  const posted = page.getByRole('dialog', { name: /Расклад оплаты/ });
  await posted.getByText(SITE).waitFor({ timeout: 10_000 });
  const recorded = await posted.innerText();
  if (!recorded.includes(accounting)) throw new Error(`в записанном раскладе нет ${accounting}: ${recorded.slice(0, 300)}`);
  await page.keyboard.press('Escape');

  // Доля легла в открытый день — счёт не заперт и правится.
  if (await page.getByText(/Счёт заперт/).count()) throw new Error('счёт с долей в открытом периоде показан запертым');
});

// ── Реестр называет учётный месяц и деньги, вошедшие в него ────────────────────────────────────────
await check('registry-names-the-accounting-month', async () => {
  const filter = { type: 'condition', column: 'Номер', op: 'eq', value: movedNumber };
  await page.goto('about:blank');
  await page.goto(`${BASE}/tables/costs.invoices/registry#filter=${encodeURIComponent(JSON.stringify(filter))}`,
    { waitUntil: 'networkidle' });
  const row = page.locator('tbody tr').filter({ hasText: movedNumber }).first();
  await row.waitFor({ timeout: 10_000 });

  // Месяц — тот, куда доля ПЕРЕНЕСЕНА, а не месяц платежа: реестр и расклад счёта обязаны сойтись.
  const month = ru(shift(through, 1)).slice(3);
  const text = (await row.innerText()).replace(/[  ]/g, ' ');
  if (!text.includes(`300,00 (${month})`)) throw new Error(`в реестре нет «300,00 (${month})»: ${text.slice(0, 300)}`);
});

// ── Счёт «раскрывается» в боковой панели: объект, доля, учётный месяц — и что из этого в отборе ────
await check('registry-row-opens-the-allocation', async () => {
  const month = ru(shift(through, 1)).slice(3);
  const filter = {
    type: 'group', logic: 'and', children: [
      { type: 'condition', column: 'Номер', op: 'eq', value: movedNumber },
      { type: 'condition', column: 'УчётныйПериод', op: 'eq', value: month },
    ],
  };
  await page.goto('about:blank');
  await page.goto(`${BASE}/tables/costs.invoices/registry#filter=${encodeURIComponent(JSON.stringify(filter))}`,
    { waitUntil: 'networkidle' });
  const row = page.locator('tbody tr').filter({ hasText: movedNumber }).first();
  await row.waitFor({ timeout: 10_000 });
  // Щелчок — по клетке с номером: в первой клетке стоит ссылка «Открыть счёт», она увела бы со страницы.
  await row.getByText(movedNumber, { exact: true }).click();

  const block = page.getByRole('region', { name: 'Разноска' });
  await block.waitFor({ timeout: 10_000 });
  const text = (await block.innerText()).replace(/[  ]/g, ' ');
  if (process.env.SHOT) await page.screenshot({ path: process.env.SHOT });
  for (const part of [SITE, month, 'в отборе', 'Счёт целиком', 'В отборе'])
    if (!text.includes(part)) throw new Error(`в блоке «Разноска» нет «${part}»: ${text.slice(0, 400)}`);

  // «В отборе» равно клетке «Сумма» этой строки: одно число, а не два похожих.
  const named = text.split(/\s*\n\s*|\t/).map(l => l.trim()).filter(Boolean);
  const total = named[named.indexOf('В отборе') + 1];
  // Сравниваем с клеткой ИМЕННО колонки «Сумма», а не ищем число в строке: «300» нашлось бы и в
  // «Сумме к оплате», и в «Суммах по периодам» — и проверка была бы зелёной при любом расхождении.
  const headers = await page.locator('thead th').allInnerTexts();
  const at = headers.findIndex(h => /^Сумма(\s*\(|$)/.test(h.trim()));
  if (at < 0) throw new Error(`в реестре нет колонки «Сумма»: ${headers.join(' | ')}`);
  const flat = value => value.replace(/\s/g, ' ').trim();
  const cell = flat(await row.locator('td').nth(at).innerText());
  if (!total || cell !== flat(total)) throw new Error(`«В отборе» — ${total}, а в клетке «Сумма» — ${cell}`);
});

// ── 3. Запертый счёт говорит почему, и правки над ним нет ──────────────────────────────────────────
// Отмена оплаты остаётся тому, у кого есть право исправления (E2, issue #1100): администратору — есть.
await check('locked-invoice-says-why', async () => {
  const refused = [];
  const listen = response => { if (response.status() >= 400) refused.push(`${response.status()} ${response.url()}`); };
  page.on('response', listen);
  try {
    await open(lockedNumber);
    await page.getByText(/Счёт заперт: период закрыт по/).waitFor({ timeout: 10_000 });
    const body = await page.locator('main').innerText();
    if (!body.includes(`закрыт по ${ru(through)} у стройки «${SITE}»`))
      throw new Error(`полоса не назвала закрытие: ${body.slice(0, 400)}`);
    if (!/Оплату отменить можно: отмена ляжет в книгу исправлений/.test(body))
      throw new Error('полоса не сказала, что оплату отменить можно');
    if (!(await page.getByRole('button', { name: 'Отменить оплату', exact: true }).count()))
      throw new Error('у запертого счёта нет «Отменить оплату», хотя право исправления у входа есть');

    for (const name of ['Сохранить', 'Сохранить строки', 'Добавить строку', 'Разобран'])
      if (await page.getByRole('button', { name, exact: true }).count())
        throw new Error(`у запертого счёта есть кнопка «${name}»`);

    if (refused.length) throw new Error(`экран запертого счёта получил отказы: ${refused.join('; ')}`);
  } finally {
    page.off('response', listen);
  }

  // И сервер держит то же: правка шапки — отказ с той же причиной.
  const view = await api('GET', `/costs/invoices/${lockedId}`);
  let error = null;
  try { await api('PUT', `/costs/invoices/${lockedId}`, { requisites: { ...view.requisites, 'Назначение': 'правка' } }); }
  catch (e) { error = e.message; }
  if (!error || !/409/.test(error) || !/заперт/.test(error)) throw new Error(`сервер правку запертого счёта не отверг: ${error}`);
});

// ── 4. Исправление запертого счёта (E2, issue #1100, PR 6b-1) ──────────────────────────────────────────
await check('admin-corrects-locked-invoice', async () => {
  const refused = [];
  const listen = r => { if (r.status() >= 400 && r.url().includes('/api/')) refused.push(`${r.status()} ${r.url()}`); };
  page.on('response', listen);
  try {
    await open(lockedNumber);
    await page.getByRole('button', { name: 'Исправить', exact: true }).click();
    const bar = page.getByTestId('correction-bar');
    await bar.waitFor({ timeout: 10_000 });
    const verify = bar.getByRole('button', { name: 'Проверить исправление…' });
    if (await verify.isEnabled()) throw new Error('проверка доступна без причины и правок');

    await page.getByLabel('Назначение', { exact: true }).fill('исправлено прогоном');
    // И строку: у неё своей записи в исправлении нет — набранное обязано уехать тем же телом.
    await page.getByLabel('Примечание, строка 1').fill('примечание исправления');
    if (await page.getByRole('button', { name: 'Сохранить строки', exact: true }).count())
      throw new Error('в исправлении у строк осталась своя кнопка записи');
    // Режим НДС исправление меняет (#1498): флажок на месте, а не пометкой «только чтение».
    const mode = page.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' });
    if (!(await mode.count()) || !(await mode.isEnabled())) throw new Error('в исправлении нет флажка «Цены без НДС, НДС сверху»');
    await bar.getByLabel(/Причина исправления/).fill('опечатка в назначении');
    await verify.click();

    const dialog = page.getByRole('dialog');
    await dialog.getByText(/Исправление ляжет днём/).waitFor({ timeout: 10_000 });
    const text = await dialog.innerText();
    if (!text.includes(`днём ${ru(today)}`)) throw new Error(`предпросмотр не назвал день исправления ${ru(today)}: ${text.slice(0, 300)}`);
    if (!/Деньги не меняются/.test(text)) throw new Error(`правка назначения показана как движение денег: ${text.slice(0, 300)}`);
    if (/Цены в строках/.test(text)) throw new Error(`режим НДС не меняли, а предпросмотр назвал его смену: ${text.slice(0, 300)}`);

    const written = page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${lockedId}/correction`)
      && r.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Записать исправление' }).click();
    const response = await written;
    if (!response.ok()) throw new Error(`запись исправления отказала: ${response.status()} ${await response.text()}`);
    // Режим едет телом всегда (#1498): без него сервер отказал бы, «не менять» из отсутствия он не читает.
    if (response.request().postDataJSON()?.vatMode !== 'Included')
      throw new Error(`тело исправления без режима счёта: ${response.request().postData()?.slice(0, 200)}`);
    await page.getByText(/Исправление записано: версия 1 ушла в историю/).waitFor({ timeout: 10_000 });
    if (await page.getByTestId('correction-bar').count()) throw new Error('после записи форма осталась в режиме исправления');

    // История на форме (PR 6b-2): записанное исправление видно сразу — с версией и причиной.
    const history = page.getByRole('region', { name: 'Исправления' });
    const row = history.getByRole('row').filter({ hasText: 'опечатка в назначении' });
    await row.waitFor({ timeout: 10_000 });
    if (!(await row.innerText()).includes('Исправление (версия 1)'))
      throw new Error(`история не назвала исправление и версию: ${await row.innerText()}`);
    if (refused.length) throw new Error(`экран исправления получил отказы: ${refused.join('; ')}`);
  } finally {
    page.off('response', listen);
  }

  // Сервер хранит ровно набранное, и счёт по-прежнему заперт: исправление закрытый месяц не открывает.
  const view = await api('GET', `/costs/invoices/${lockedId}`);
  if (view.requisites['Назначение'] !== 'исправлено прогоном')
    throw new Error(`назначение после исправления — «${view.requisites['Назначение']}»`);
  if (view.lines[0]?.note !== 'примечание исправления')
    throw new Error(`примечание строки после исправления — «${view.lines[0]?.note}»: строки не уехали с исправлением`);
  if (view.payment.lockedBy === null) throw new Error('после исправления счёт перестал быть запертым');
});

}
} finally {
  // Закрытие — только своё и только до конца прогона: соседним наборам оно не остаётся.
  // Проверкой, а не строкой в журнале: неотменённое закрытие — провал прогона, его обязан увидеть итог.
  if (site && closedThrough)
    await check('own-closure-is-reopened', () => api('POST', '/periods/reopen', {
      contour: 'construction', constructionId: site.id, ifMatch: closedThrough, reason: 'живой прогон оплаты счёта',
    }));
  await browser.close();
}

process.exitCode = summarize('Оплата счёта');
