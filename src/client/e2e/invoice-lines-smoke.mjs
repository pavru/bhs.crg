// Живой прогон СТРОК СЧЁТА (задача C2 этапа 2, issue #1078, ТЗ COST-7, COST-7.2, COST-6.2).
//
// ЗАЧЕМ ОН ЕСТЬ. Шесть утверждений этой части нечем проверить ни типами, ни юнит-тестами — каждое про
// поведение целиком: строка, пережившая ПЕРЕЗАГРУЗКУ; таблица из буфера, у которой шапка не стала
// строкой счёта; расхождение сумм, показанное числом и не мешающее сохранить; отказ «разобран» с
// названной строкой; выбор позиции и разноска строки, после которых счёт разбирается; отбор
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

  // ── 5. Позиция выбирается, строка разносится — и счёт разбирается ────────────
  //
  // ⚠️ С F1 (issue #1085) «разобран» требует и разноски: строка без неё — остаток «не разнесено», и
  // переход обязан отказать, назвав строку. Сумма к оплате равна сумме строки — иначе отказ пришёл бы
  // за расхождение, и проверка разноски прошла бы вхолостую.
  await check('позиция выбирается из справочника, строка разносится, и после этого счёт разбирается', async () => {
    const number = `СЧ-Н${stamp}`;
    await invoice(number, 1_000);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('2');
    await cell('Цена', 1).fill('500');

    await page.getByRole('button', { name: 'выбрать позицию' }).click();
    await page.getByPlaceholder('часть наименования').fill(stamp);
    await page.getByRole('button', { name: cable }).click();
    await saveLines();

    // Не разнесено — «Разобран» отказывает, и форма говорит об этом до нажатия.
    // ⚠️ Именно ждём, а не считаем сразу: сохранение строк возвращает управление по ответу PUT, а
    // подпись рисуется из сводки разноски, которую форма перечитывает СЛЕДОМ. Мгновенный подсчёт
    // состязался с этим чтением и через раз проигрывал — проверка краснела на исправной форме.
    await page.getByText('разнесена не полностью', { exact: false }).first()
      .waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('у формы не сказано, что строка не разнесена'); });

    const refused = page.waitForResponse(
      r => r.request().method() === 'POST' && r.url().endsWith('/parsed'), { timeout: 15_000 });
    await page.getByRole('button', { name: 'Разобран' }).click();
    if ((await refused).status() !== 400)
      throw new Error('«Разобран» прошёл у счёта, строка которого не разнесена по стройкам');

    // Разноска — через диалог строки: стройка из посева, всё количество.
    await page.getByRole('button', { name: 'Разноска, строка 1' }).click();
    await page.getByLabel('Куда, часть 1', { exact: true }).selectOption({ label: 'Демо-стройка' });
    await page.getByLabel('Количество, часть 1', { exact: true }).fill('2');
    const allocated = page.waitForResponse(
      r => r.request().method() === 'PUT' && r.url().endsWith('/allocation'), { timeout: 15_000 });
    await page.getByRole('button', { name: 'Сохранить разноску' }).click();
    const answer = await allocated;
    if (!answer.ok()) throw new Error(`разноска не сохранилась: ${answer.status()}`);
    await page.getByText('Разноска сходится', { exact: false }).waitFor({ timeout: 10_000 });

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
  // ── 6а. Двое правят один счёт: второй получает отказ, а набранное остаётся (issue #1176) ──
  //
  // До правки сервер сверял версию, прочитанную тем же запросом, и второй затирал строки первого
  // молча. Проверяется глазами человека: отказ назван, поле держит набранное, а «Перечитать счёт»
  // показывает то, что лежит в базе. «Сосед» — запрос самого прогона: он называет свежую версию.
  await check('чужая правка строк: сохранение отказывает, набранное цело, «Перечитать счёт» показывает сохранённое', async () => {
    const number = `СЧ-В${stamp}`;
    const created = await invoice(number);
    const lines = `/costs/invoices/${created.id}/lines`;
    await api('PUT', lines, { lines: [{ supplierText: 'первая строка', quantity: '1', price: '10' }] });
    await open(number);

    await cell('Количество', 1).fill('7');
    await api('PUT', lines, { lines: [{ supplierText: 'строка соседа', quantity: '3', price: '10' }] });

    const [refusal] = await Promise.all([
      page.waitForResponse(r => r.url().endsWith(lines) && r.request().method() === 'PUT'),
      page.getByRole('button', { name: 'Сохранить строки' }).click(),
    ]);
    if (refusal.status() !== 409) throw new Error(`устаревшие строки записались: ответ ${refusal.status()}`);

    const notice = page.getByRole('alert').filter({ hasText: 'Счёт тем временем изменили' });
    await notice.waitFor({ timeout: 10_000 });
    if ((await cell('Количество', 1).inputValue()) !== '7')
      throw new Error('после отказа набранное количество пропало из поля');

    await page.getByRole('button', { name: 'Перечитать счёт' }).click();
    await notice.waitFor({ state: 'detached', timeout: 10_000 });
    const shown = [await cell('Наименование в счёте', 1).inputValue(), await cell('Количество', 1).inputValue()];
    if (shown.join('|') !== 'строка соседа|3') throw new Error(`после перечитывания на экране не сохранённое: ${shown}`);

    // Версия у счёта одна, а части разные: сосед поправил ШАПКУ — строки, которых он не трогал,
    // сохраняются. Форма называет версию своего вида, получает отказ, перечитывает счёт, видит, что
    // её строк не трогали, и повторяет запись со свежей версией: человек отказа не видит.
    const saved = url => page.waitForResponse(
      r => r.url().endsWith(url) && r.request().method() === 'PUT' && r.status() === 200, { timeout: 10_000 });
    const noNotice = async what => {
      if (await notice.count() > 0) throw new Error(`${what}: полоса «счёт изменили» показана без причины`);
    };

    await cell('Количество', 1).fill('9');
    const seenByNeighbour = await api('GET', `/costs/invoices/${created.id}`);
    await api('PUT', `/costs/invoices/${created.id}`,
      { requisites: { ...seenByNeighbour.requisites, 'Назначение': 'правка соседа' } });
    await Promise.all([
      saved(lines).catch(() => { throw new Error('правка шапки соседом отказала сохранению строк'); }),
      page.getByRole('button', { name: 'Сохранить строки' }).click(),
    ]);
    await noNotice('сосед правил шапку, я — строки');

    // И наоборот (ревью PR #1208): я правлю ШАПКУ, сосед меняет строки. Подпись шапки строилась по
    // набору правленых полей и менялась с первой же правкой — любая смена версии читалась как «устарело».
    await page.getByLabel('Номер', { exact: true }).fill(`${number}-П`);
    await api('PUT', lines, { lines: [{ supplierText: 'строка соседа, вторая правка', quantity: '4', price: '10' }] });
    await Promise.all([
      saved(`/costs/invoices/${created.id}`).catch(() => { throw new Error('правка строк соседом отказала сохранению шапки'); }),
      page.getByRole('button', { name: 'Сохранить', exact: true }).click(),
    ]);
    await noNotice('сосед правил строки, я — шапку');
    // Строки при этом на экране — соседа: своих правок в них не было, и таблица пересобралась.
    await page.waitForFunction(() => [...document.querySelectorAll('input')]
      .some(input => input.value === 'строка соседа, вторая правка'), null, { timeout: 10_000 });
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
  // ── 8. Удалённую позицию есть чем снять ──────────────────────────────────────
  //
  // ⚠️ Сторож находки ревью, и находка была ТУПИКОМ. Форма считала потерей любое пустое название, а
  // кнопку снятия у такой клетки прятала. Сервер же отказывается сохранять строку с битой ссылкой —
  // значит строку нельзя было ни сохранить, ни починить: счёт застревал целиком. Теперь потерю
  // называет сервер, а снять ссылку можно всегда, пока она есть.
  await check('занятую позицию удалить нельзя, а потерянную ссылку есть чем снять', async () => {
    const doomed = await position(`Позиция под снос ${stamp}`);

    const number = `СЧ-У${stamp}`;
    await invoice(number);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('1');
    await cell('Цена', 1).fill('10');
    await page.getByRole('button', { name: 'выбрать позицию' }).click();
    await page.getByPlaceholder('часть наименования').fill(`Позиция под снос ${stamp}`);
    await page.getByRole('button', { name: `Позиция под снос ${stamp}` }).click();
    await saveLines();

    // Позицию, стоящую в строке счёта, справочник больше не отдаёт (issue #1094): отказ называет,
    // кто держит. До правки удаление проходило, и этот прогон сам получал потерянную ссылку именно им.
    const refusal = await api('DELETE', `/common-data/${doomed}`).then(() => null, e => String(e));
    if (refusal === null) throw new Error('позиция, стоящая в строке счёта, удалилась');
    if (!refusal.includes('409') || !refusal.includes('строки счетов'))
      throw new Error(`отказ не называет держателя: ${refusal}`);

    // Потерянная ссылка теперь приходит только мимо удаления — восстановлением копии, гонкой с
    // записью модуля, — и через адреса её не получить. Экран проверяем на ответе сервера о потере:
    // он подменяется здесь, а что сервер так отвечает на настоящую потерю, стережёт InvoiceLineTests.
    const asLost = async route => {
      const response = await route.fetch();
      const view = await response.json();
      for (const line of view.lines ?? [])
        if (line.nomenclatureId) { line.nomenclatureName = null; line.nomenclatureLost = true; }
      await route.fulfill({ response, json: view });
    };
    const invoiceRead = url => /\/api\/costs\/invoices\/[0-9a-f-]{36}$/.test(url.pathname);
    await page.route(invoiceRead, route => (route.request().method() === 'GET' ? asLost(route) : route.continue()));

    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    await page.getByRole('button', { name: 'позиция удалена' }).waitFor({ timeout: 10_000 });

    // Потеря бывает за краем экрана — строка под прокруткой. Шапка говорит о ней сама (issue #1184).
    const note = page.getByRole('note').filter({ hasText: 'Ссылки на удалённые записи справочников' });
    await note.waitFor({ timeout: 10_000 });
    if (!(await note.innerText()).includes('позиция в строке 1'))
      throw new Error(`сводка не называет строку с потерянной позицией: ${await note.innerText()}`);

    // Главное: выход есть. Снимаем ссылку и сохраняем — до правки сервер отказывал, а снять было нечем.
    await page.getByRole('button', { name: 'Снять позицию' }).first().click();
    await page.unroute(invoiceRead);
    await saveLines();

    await page.reload({ waitUntil: 'networkidle' });
    await open(number);
    await page.getByRole('button', { name: 'выбрать позицию' }).first().waitFor({ timeout: 10_000 });
    if ((await page.getByRole('button', { name: 'позиция удалена' }).count()) !== 0)
      throw new Error('после снятия ссылки строка всё ещё считает позицию потерянной');
  });

} finally {
  await browser.close();
}

// ⚠️ Код возврата, а не `process.exit()`: тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл теряются как раз итоговые строки — те, по которым прогон читают в CI.
//
// ⚠️ Значение `summarize` отдаётся КАК ЕСТЬ: это готовый код возврата (0 — прошло), хотя читается как
// «получилось ли». Здесь стояло `process.exit(summarize(…) ? 0 : 1)` — то есть наоборот: зелёный
// прогон ронял работу, а КРАСНЫЙ отчитался бы успехом, потому что `run-suites` судит по коду
// возврата, а не по напечатанному. Сверяет `suites.test.mjs`.
process.exitCode = summarize('Строки счёта');
