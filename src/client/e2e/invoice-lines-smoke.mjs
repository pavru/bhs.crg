// Живой прогон СТРОК СЧЁТА (задача C2 этапа 2, issue #1078, ТЗ COST-7, COST-7.2, COST-6.2).
//
// ЗАЧЕМ ОН ЕСТЬ. Шесть утверждений этой части нечем проверить ни типами, ни юнит-тестами — каждое про
// поведение целиком: строка, пережившая ПЕРЕЗАГРУЗКУ; таблица из буфера, у которой шапка не стала
// строкой счёта; расхождение сумм, показанное числом и не мешающее сохранить; отказ «разобран» с
// названной строкой; выбор позиции из справочника, после которого счёт разбирается; отбор
// «Разобрать», в котором счёт виден. Юнит-тест на «строка пережила открытие» проверял бы собственную
// выдумку: пережить её обязан ответ сервера, а не состояние компонента.
//
// ⚠️ ДАННЫЕ ПРОГОН ГОТОВИТ СЕБЕ САМ — и счета, и позиции номенклатуры (со своей приметой в имени).
// Посеянные не годятся: первый же прогон разобрал бы их, и второй запуск на той же базе проверял бы
// разобранное, оставаясь зелёным.
//
// ⚠️ Элементы ищутся ПО ВИДИМОМУ ТЕКСТУ, ролям и подписям полей — без служебных атрибутов. Так
// проверяется заодно то, что человек вообще видит нужные слова.
//
// Требует поднятых фронта и бэка, посеянной организации и ВКЛЮЧЁННОГО модуля `costs`
// (`Modules__Enabled=id,costs`) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/invoice-lines-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);

await login(page);
await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });

if ((await page.getByRole('heading', { name: 'Счета на оплату' }).count()) === 0) {
  console.error('Экрана счетов нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs)? '
    + 'Без него проверять нечего, и зелёный прогон был бы отчётом о работе, которой не было.');
  await browser.close();
  process.exit(1);
}

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

const organizations = await api('GET', '/costs/organizations');
if (organizations.length === 0) {
  console.error('В справочнике нет ни одной организации — посев не отработал. Проверять нечего.');
  await browser.close();
  process.exit(1);
}
const supplierId = organizations[0].id;

/**
 * Тип номенклатуры. Его поднимает миграция ядра над «Материалом» (issue #963), а там, где
 * «Материала» нет вовсе (CI, чистая установка), — посев: без типа выбирать позицию не из чего ни
 * прогону, ни человеку.
 *
 * Нет типа — прогон отказывается работать ВСЛУХ: молчаливый пропуск отдал бы зелёный отчёт о трёх
 * проверках, которые не выполнялись.
 */
const types = await api('GET', '/document-types');
const nomenclatureType = types.find(t => t.code === 'Номенклатура');
if (!nomenclatureType) {
  console.error('Типа «Номенклатура» в базе нет — выбирать позицию не из чего. Его поднимает '
    + 'миграция ядра над «Материалом», а на базе без «Материала» — посев (npm run test:e2e:seed). '
    + 'Прогон остановлен: без типа три проверки из шести проверяли бы пустоту.');
  await browser.close();
  process.exit(1);
}

/** Своя позиция номенклатуры — с приметой прогона в имени, чтобы поиск находил ровно её. */
async function position(name) {
  const created = await api('POST', '/common-data', {
    displayName: name, compositeTypeId: nomenclatureType.id, data: '{}',
    scope: 'System', scopeId: null, aliases: [],
  });
  return created.id;
}

const cable = `Кабель прогона ${stamp}`;
await position(cable);

/** Счёт с полной шапкой: без плательщика «разобран» откажет обязательным полем, а проверяем не его. */
async function invoice(number, total = 10_000) {
  const payers = organizations.length > 1 ? organizations[1].id : supplierId;
  return api('POST', '/costs/invoices', {
    requisites: {
      'Номер': number,
      'Дата': '2026-09-29',
      'Поставщик': { $ref: 'catalog', entryId: supplierId },
      'Плательщик': { $ref: 'catalog', entryId: payers },
      'Итого': total,
    },
  });
}

/** Открыть счёт списка по номеру и дождаться формы. */
async function open(number) {
  await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
  await page.getByRole('button', { name: 'Сохранить строки' }).waitFor({ timeout: 10_000 });
}

/** Заполнить клетку строки — по подписи, которую видит и человек с экранного чтения. */
const cell = (label, number) => page.getByLabel(`${label}, строка ${number}`, { exact: true });

/**
 * Дописать строку и ДОЖДАТЬСЯ её появления.
 *
 * ⚠️ Ожидание своё и длиннее обычного: первая проверка идёт по холодному дев-серверу, который
 * собирает модули на первом заходе, и на нём десяти секунд не хватило один раз из двух. Дожидаться
 * молча нельзя — провал «getByLabel timeout» не говорит, чего не дождались.
 */
async function addRow(number) {
  await page.getByRole('button', { name: 'Добавить строку' }).click();
  try {
    await cell('Количество', number).waitFor({ timeout: 20_000 });
  } catch {
    throw new Error(`строка ${number} не появилась после нажатия «Добавить строку»`);
  }
}

/**
 * Сохранить строки и дождаться ОТВЕТА сервера.
 *
 * ⚠️ Погасшая кнопка признаком НЕ ГОДИТСЯ, хотя выглядит им: она гаснет и на время самого запроса
 * (`disabled || loading`), то есть ожидание «погасла» срабатывает ДО ответа. Дальше идёт либо
 * перезагрузка — она ОБРЫВАЕТ запрос, и строки теряются по вине прогона, — либо переход «Разобран»,
 * который отказал бы, не увидев ещё не записанных строк. Тем же ожиданием упали в CI две проверки
 * формы счёта, причём на мастере, где сохранение работает (разбор прогонов #1078).
 */
async function saveLines() {
  const answered = page.waitForResponse(
    r => r.request().method() === 'PUT' && r.url().endsWith('/lines'), { timeout: 15_000 });
  await page.getByRole('button', { name: 'Сохранить строки' }).click();
  const answer = await answered;
  if (!answer.ok()) throw new Error(`строки не сохранились: ${answer.status()}`);
}

try {
  // ── 1. Строка заводится с клавиатуры и переживает перезагрузку ───────────────
  await check('строка заводится с клавиатуры и переживает перезагрузку страницы', async () => {
    const number = `СЧ-Л${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await cell('Наименование в счёте', 1).fill('Кабель из бумаги поставщика');
    await cell('Количество', 1).fill('100');
    await cell('Цена', 1).fill('48,50');
    await saveLines();

    // ⚠️ ПЕРЕЗАГРУЗКА: без неё проверка подтверждала бы состояние живого компонента.
    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    const quantity = await cell('Количество', 1).inputValue();
    if (quantity !== '100') throw new Error(`после перезагрузки количество «${quantity}», ожидалось 100`);
    // Сумму никто не набирал — её досчитал СЕРВЕР по количеству и цене.
    const amount = await cell('Сумма', 1).inputValue();
    if (amount !== '4850') throw new Error(`сумма «${amount}», ожидалась досчитанная 4850`);
  });

  // ── 2. Вставка из буфера: шапка не становится строкой счёта ──────────────────
  await check('вставка из буфера даёт строки таблицей, а шапка в счёт не идёт', async () => {
    const number = `СЧ-Б${stamp}`;
    await invoice(number);
    await open(number);

    await page.getByRole('button', { name: 'Вставить из буфера' }).click();
    await page.getByLabel('Таблица из буфера').fill([
      'Наименование\tЕд.\tКол-во\tЦена\tСумма',
      'Кабель ВВГнг-LS 3х2,5\tм\t100\t48,50\t4850,00',
      'Труба гофрированная 20 мм\tм\t50\t12,00\t600,00',
    ].join('\n'));

    // Число в кнопке — это и есть утверждение «шапка в счёт не пойдёт»: строк три, добавятся две.
    await page.getByRole('button', { name: 'Добавить строк: 2' }).click();
    await saveLines();

    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    const second = await cell('Наименование в счёте', 2).inputValue();
    if (second !== 'Труба гофрированная 20 мм')
      throw new Error(`вторая строка «${second}», ожидалась труба`);
    if ((await cell('Наименование в счёте', 3).count()) !== 0)
      throw new Error('появилась третья строка — шапка таблицы попала в счёт');
  });

  // ── 3. Расхождение сумм — числом и НЕ запретом ───────────────────────────────
  await check('расхождение суммы строк с суммой к оплате показано числом и не мешает сохранить', async () => {
    const number = `СЧ-Р${stamp}`;
    await invoice(number, 10_000);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('1');
    await cell('Цена', 1).fill('7000');

    await page.getByText(/расхождение/).waitFor({ timeout: 10_000 });
    // Сохранение прошло — значит расхождение ничего не запретило.
    await saveLines();
  });

  // ── 4. «Разобран» отказывает, пока строка ждёт позиции, и НАЗЫВАЕТ строку ────
  //
  // Кнопка при этом не приглушена нарочно: приглушённая молчит о причине, а причин пять, и знает их
  // сервер. Проверяется именно отказ с объяснением.
  await check('«Разобран» отказывает, пока строка ждёт позиции, и называет строку', async () => {
    const number = `СЧ-О${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await cell('Наименование в счёте', 1).fill('Позиции нет — ждёт разбора');
    await cell('Количество', 1).fill('1');
    await cell('Цена', 1).fill('100');
    await saveLines();

    await page.getByRole('button', { name: 'Разобран' }).click();

    // Текст отказа читаем ЦЕЛИКОМ и сверяем сами. Ждать его через getByText нельзя строгим режимом:
    // тост живёт в двух узлах — видимом и объявлении для чтения с экрана. А главное, так провал
    // говорит, ЧТО пришло вместо ожидаемого: «таймаут» о причине отказа не сообщает ничего.
    const toasts = page.locator('[role="region"][aria-label*="Notification"], ol').first();
    await toasts.getByText(/Счёт .*:/).first().waitFor({ timeout: 10_000 });
    const said = (await toasts.innerText()).replace(/\s+/g, ' ');
    if (!/строка 1/.test(said))
      throw new Error(`отказ не назвал строку, пришло: «${said.slice(0, 220)}»`);
  });

  // ── 5. Позиция выбирается из справочника — и счёт разбирается ────────────────
  await check('позиция выбирается из справочника, и после этого счёт разбирается', async () => {
    const number = `СЧ-Н${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('2');
    await cell('Цена', 1).fill('500');

    await page.getByRole('button', { name: 'выбрать позицию' }).click();
    await page.getByPlaceholder('часть наименования').fill(stamp);
    await page.getByRole('button', { name: cable }).click();
    await saveLines();

    await page.getByRole('button', { name: 'Разобран' }).click();
    // Состояние показано человеку словом — оно же и признак перехода.
    await page.getByRole('button', { name: 'Вернуть в черновик' }).waitFor({ timeout: 10_000 });
    if ((await page.getByText('Счёт разобран', { exact: false }).count()) === 0)
      throw new Error('после перехода на форме не сказано, что счёт разобран');
  });

  // ── 6. Отбор «Только Разобрать» показывает счёт со строкой без позиции ───────
  await check('отбор «Только Разобрать» оставляет счета, у которых строки ждут позиции', async () => {
    const waiting = `СЧ-Ж${stamp}`;
    const created = await invoice(waiting);
    await api('PUT', `/costs/invoices/${created.id}/lines`, {
      lines: [{ supplierText: 'ждёт позиции', quantity: '1', price: '10' }],
    });

    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.getByLabel('Только «Разобрать»').check();
    await page.getByRole('button', { name: new RegExp(waiting) }).first()
      .waitFor({ timeout: 10_000 });

    // А разобранный счёт из очереди уходит: иначе очередь перестала бы быть очередью.
    if ((await page.getByRole('button', { name: new RegExp(`СЧ-Н${stamp}`) }).count()) !== 0)
      throw new Error('в отборе «Разобрать» остался счёт, у которого все строки разобраны');
  });
  // ── 7. Неполный список позиций НАЗВАН неполным ───────────────────────────────
  //
  // Отсечение, о котором промолчали, читается как «такой позиции нет» — и человек заводит вторую
  // такую же позицию номенклатуры. Сводить затраты после этого придётся вручную, поэтому оговорка
  // проверяется глазами человека, а не только признаком в ответе сервера.
  await check('неполный список позиций назван неполным', async () => {
    const prefix = `Реле прогона ${stamp}`;
    // На одну больше, чем отдаёт адрес (25): ровно столько, чтобы «есть ещё» стало фактом.
    for (let index = 0; index < 26; index += 1) await position(`${prefix} ${String(index).padStart(2, '0')}`);

    const number = `СЧ-М${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await page.getByRole('button', { name: 'выбрать позицию' }).click();
    await page.getByPlaceholder('часть наименования').fill(prefix);

    await page.getByText(/подходящих больше/).waitFor({ timeout: 10_000 });
  });
} finally {
  await browser.close();
}

process.exit(summarize('Строки счёта') ? 0 : 1);
