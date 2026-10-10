// Живой прогон ФОРМЫ СЧЁТА (задача C1 этапа 2, issue #1076, ТЗ COST-6.2).
//
// ЗАЧЕМ ОН ЕСТЬ. Пять утверждений этой формы нечем проверить ни типами, ни юнит-тестами: каждое — про
// поведение целиком. Сохранение без единого заполненного поля; метка, пережившая ПЕРЕЗАГРУЗКУ
// страницы; снятие меток одного блока при сохранности соседнего; скан рядом с формой и честная
// замена панели на узком экране; оговорка о дубликате, которая не запрещает. Юнит-тест на «метка
// переживает открытие» проверял бы собственную выдумку: пережить её обязан ответ сервера, а не
// состояние компонента — и прежняя версия механизма была зелёной ровно так.
//
// ⚠️ ДАННЫЕ ПРОГОН ГОТОВИТ СЕБЕ САМ, адресами приложения — и метки, и скан, и пару дубликатов.
// Посеянные счета для этого не годятся: первый же прогон снял бы с них метки, и второй запуск на той
// же базе проверял бы пустоту, оставаясь зелёным. От посева нужна только организация: поставщика
// модуль не заводит, его ведёт человек.
//
// ⚠️ Элементы ищутся ПО ВИДИМОМУ ТЕКСТУ и ролям, без служебных атрибутов. Так проверяется заодно то,
// что человек вообще видит нужные слова: метка, названная только атрибутом, для него не существует.
//
// Требует поднятых фронта и бэка, посеянных данных и ВКЛЮЧЁННОГО модуля `costs`
// (`Modules__Enabled=id,costs`) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/invoices-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

/** Подпись у помеченного поля — она же примета метки на экране. */
const MARK = 'Распознано, не подтверждено';

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

/** Запрос к приложению ТЕМ ЖЕ токеном, что у открытой страницы: прогон готовит себе данные. */
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

/**
 * Реквизиты с уникальным номером: два прогона подряд не должны считать друг друга дубликатом.
 *
 * ⚠️ Поставщик обязателен. Дубликатом счёт считается по ТРОЙКЕ «поставщик + номер + дата», и без
 * поставщика оговорка не появляется вовсе — проверка искала бы её и не находила, виня форму.
 */
const requisites = (number, purpose) => ({
  'Номер': number,
  'Дата': '2026-09-03',
  'Поставщик': { $ref: 'catalog', entryId: supplierId },
  'Итого': 42000,
  'Срок': '2026-10-03',
  'Назначение': purpose,
});

/** Любая организация справочника — годится первая: проверки не про выбор поставщика. */
const organizations = await api('GET', '/costs/organizations?purpose=choice');
if (organizations.length === 0) {
  console.error('В справочнике нет ни одной организации — посев не отработал. Проверять нечего.');
  await browser.close();
  process.exit(1);
}
const supplierId = organizations[0].id;

/**
 * Ожидание со СВОИМ именем: голый таймаут говорит «10000ms exceeded» и не говорит, ЧЕГО не
 * дождались. В прогоне из пяти проверок это стоит отдельного разбора при каждом провале.
 */
async function named(what, run) {
  try { await run(); }
  catch (e) { throw new Error(`${what} (${e.message.split('\n')[0]})`); }
}

/**
 * Открыть счёт списка по номеру и дождаться формы.
 *
 * ⚠️ Кнопка ищется ТОЧНЫМ совпадением имени: на форме их две — «Сохранить» у шапки и
 * «Сохранить строки» у таблицы строк (C2, issue #1078). По подстроке находились бы обе, и прогон
 * падал бы строгим режимом Playwright, виня форму.
 */
async function open(number) {
  await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
  await page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 10_000 });
}

/**
 * Нажать «Сохранить» и дождаться ОТВЕТА сервера.
 *
 * ⚠️ Без ожидания ответа проверка мигает, и мигает обманчиво: `reload()` сразу после нажатия
 * ОБРЫВАЕТ запрос — правка «не сохраняется» по вине прогона, а сообщение винит форму. Так в CI упали
 * две проверки из шести, причём на мастере, где сохранение работает (разбор прогонов #1078).
 *
 * ⚠️ Ждать здесь нечего, кроме ответа: успешное сохранение видимого следа не оставляет (тост заведён
 * для отказа), а кнопка гаснет и на время самого запроса (`disabled || loading`) — «погасла»
 * срабатывает ДО ответа, то есть такое ожидание не ловит вовсе ничего.
 */
async function save() {
  const answered = page.waitForResponse(
    r => ['PUT', 'POST'].includes(r.request().method())
      && /\/api\/costs\/invoices(\/[0-9a-f-]+)?$/.test(r.url()),
    { timeout: 15_000 });
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  const answer = await answered;
  // Отказ называем отказом: иначе он доедет до проверки как «правка не сохранилась», то есть будет
  // выглядеть поломкой формы, а не отказом сервера с кодом и причиной.
  if (!answer.ok()) throw new Error(`сохранение отказало: ${answer.status()}`);
}

/** Сколько меток видно на форме. */
const markCount = () => page.getByText(MARK, { exact: true }).count();

/**
 * Приложить скан — многочастным запросом, тем же адресом, что и форма.
 *
 * Свой скан, а не посеянный: у посеянного счёта есть двойник с тем же номером и БЕЗ скана, и выбор
 * «первого по номеру» приводил бы то к одному, то к другому. Проверка, зависящая от порядка строк в
 * списке, краснеет через раз и выглядит поломкой формы.
 */
async function attachScan(id) {
  await page.evaluate(async ([id, base64]) => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const form = new FormData();
    form.append('file', new Blob([bytes], { type: 'image/png' }), 'скан.png');
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const res = await fetch(`/api/costs/invoices/${id}/scan`,
      { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form });
    if (!res.ok) throw new Error(`скан не приложился: ${res.status} ${await res.text()}`);
  }, [id, PNG_BASE64]);
}

/** PNG 1×1 — скан для проверки панели. Собран здесь: бинаря в репозитории быть не должно. */
const PNG_BASE64 =
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

try {
  // ── 1. Черновик сохраняется без строк ───────────────────────────────────────
  //
  // Строк в первой версии нет вовсе, и проверка именно об этом: форма не требует НИЧЕГО. Заведи
  // кто-нибудь обязательность — «Новый счёт» перестал бы заводить счёт, а заказчику черновик нужен
  // до того, как он посмотрел бумагу.
  await check('черновик заводится пустым и сохраняется с одним номером', async () => {
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Новый счёт' }).click();
    await page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 10_000 });

    const number = `СЧ-Ч${stamp}`;
    await page.getByLabel('Номер', { exact: true }).fill(number);
    await save();

    // Сохранилось — если счёт появился в списке под своим номером.
    await page.getByRole('button', { name: new RegExp(number) }).first()
      .waitFor({ timeout: 10_000 });
  });

  // ── 2. Метки переживают повторное открытие ──────────────────────────────────
  //
  // ⚠️ Между шагами страница ПЕРЕЗАГРУЖАЕТСЯ. Без перезагрузки проверка подтверждала бы состояние
  // живого компонента — ровно то, что было раньше и что не переживало ничего.
  await check('метки «распознано, не подтверждено» переживают перезагрузку страницы', async () => {
    const number = `СЧ-П${stamp}`;
    await api('POST', '/costs/invoices', {
      requisites: requisites(number, 'Метки переживают перезагрузку'),
      unconfirmed: ['Номер', 'Итого', 'Срок'],
    });

    await open(number);
    const first = await markCount();
    if (first !== 3) throw new Error(`после создания меток ${first}, ожидалось 3`);

    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    const second = await markCount();
    if (second !== 3) throw new Error(`после перезагрузки меток ${second}, ожидалось 3`);
  });

  // ── 3. «Всё верно» снимает метки блока, а не все ────────────────────────────
  await check('«Всё верно» снимает метки своего блока и не трогает соседний', async () => {
    const number = `СЧ-В${stamp}`;
    await api('POST', '/costs/invoices', {
      requisites: requisites(number, 'Подтверждение по блоку'),
      unconfirmed: ['Номер', 'Итого', 'Срок'],
    });

    await open(number);
    await page.getByRole('button', { name: /Всё верно \(2\)/ }).click();

    // Ждём ИСЧЕЗНОВЕНИЯ кнопки шапки. Кнопку соседнего блока ждать нельзя: она была на месте и до
    // нажатия, то есть дождалась бы мгновенно, и проверка мерила бы экран до ответа сервера.
    await page.getByRole('button', { name: /Всё верно \(2\)/ }).waitFor(
      { state: 'detached', timeout: 10_000 });
    if ((await page.getByRole('button', { name: /Всё верно \(1\)/ }).count()) !== 1)
      throw new Error('у соседнего блока не осталось кнопки «Всё верно» — сняли лишнее');

    const left = await markCount();
    if (left !== 1) throw new Error(`осталось меток ${left}, ожидалась одна — у «Оплатить до»`);
  });

  // ── 4. Скан открывается РЯДОМ, а на узком экране честно заменяется ──────────
  await check('скан показан рядом с формой, а ниже 1280 замена названа причиной', async () => {
    const number = `СЧ-С${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'Со сканом') });
    await attachScan(created.id);

    await open(number);
    await page.getByRole('button', { name: 'Во весь экран' }).waitFor({ timeout: 10_000 });

    await page.setViewportSize({ width: 1100, height: 1000 });
    await page.getByText('не меньше 1280').waitFor({ timeout: 10_000 });
    if ((await page.getByRole('button', { name: 'Во весь экран' }).count()) !== 0)
      throw new Error('панель скана осталась на экране уже 1280');

    await page.setViewportSize({ width: 1500, height: 1000 });
    await page.getByRole('button', { name: 'Во весь экран' }).waitFor({ timeout: 10_000 });
  });

  // ── 5. Поставщика можно СНЯТЬ ───────────────────────────────────────────────
  //
  // Выпадающий список пустого значения не отдаёт, поэтому без отдельного пункта «не выбрано» снять
  // ссылку нечем: ошибочно распознанный плательщик остался бы в счёте навсегда. Проверяется
  // перезагрузкой — сохранилось ли снятие, а не как выглядит поле.
  await check('поставщика можно снять, и снятие сохраняется', async () => {
    const number = `СЧ-С${stamp}-2`;
    await api('POST', '/costs/invoices', { requisites: requisites(number, 'Снятие поставщика') });

    await open(number);
    await page.getByLabel('Поставщик').click();
    await page.getByRole('option', { name: '— не выбрано —' }).click();
    await save();

    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    const list = await api('GET', '/costs/invoices');
    const saved = list.find(i => i.number === number);
    if (saved.supplierId !== null)
      throw new Error(`поставщик остался: ${saved.supplierId}`);
  });

  // ── 6. Дубликат — оговорка со ссылкой, а не запрет ──────────────────────────
  await check('оговорка о дубликате ведёт на тот счёт и не мешает сохранению', async () => {
    const number = `СЧ-Д${stamp}`;
    await api('POST', '/costs/invoices', { requisites: requisites(number, 'Счёт первый') });
    await api('POST', '/costs/invoices', { requisites: requisites(number, 'Счёт второй') });

    // Открываем ВТОРОЙ по созданию — в списке он ПЕРВЫЙ: реестр отдаётся по дате счёта и времени
    // создания, оба по убыванию. Даты у пары одинаковые, значит порядок решает создание.
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: new RegExp(number) }).first().click();
    await page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 10_000 });
    await page.getByText('Похоже на дубликат').waitFor({ timeout: 10_000 });

    // Сохранение НЕ заблокировано: правка проходит при живой оговорке.
    await page.getByLabel('Назначение').fill('Правка при дубликате');
    if (await page.getByRole('button', { name: 'Сохранить', exact: true }).isDisabled())
      throw new Error('сохранение заблокировано дубликатом');
    await save();

    // ⚠️ Сохранение проверяется ПЕРЕЗАГРУЗКОЙ, а не погасшей кнопкой. Кнопка гаснет и на время
    // самого запроса (`disabled || loading`), поэтому ожидание «погасла» срабатывало бы ДО ответа
    // сервера — то есть проверка не могла упасть вовсе, даже если сохранение отказало.
    await page.reload({ waitUntil: 'networkidle' });
    await page.getByRole('button', { name: new RegExp(number) }).first().click();
    await page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 10_000 });

    const saved = await page.getByLabel('Назначение').inputValue();
    if (saved !== 'Правка при дубликате')
      throw new Error(`правка при дубликате не сохранилась: в поле «${saved}»`);

    // Ссылка ведёт на ТОТ счёт — узнаём его по назначению, оно у счетов разное.
    await page.locator('button', { hasText: new RegExp(`${number} от`) }).first().click();
    await named('ссылка не открыла тот счёт', () => page.waitForFunction(
      () => Array.from(document.querySelectorAll('input')).some(i => i.value === 'Счёт первый'),
      null, { timeout: 10_000 }));

    // Правка осталась у ТОГО счёта, который правили: ссылка открывает соседний, а не переносит
    // изменения. Проверяется здесь же — разойдись это, счёт менялся бы не тот, о чём никто не узнал.
    if ((await page.getByLabel('Назначение').inputValue()) !== 'Счёт первый')
      throw new Error('после перехода по ссылке в форме не тот счёт');
  });

  // ── 7. Счёт из скана: неудача НАЗВАНА, а не выглядит пустым черновиком (issue #1077) ────────────
  //
  // Сторож задачи. Файл заведомо не читается (от PDF в нём одна подпись с версией — без неё сервер его не
  // принял бы вовсе: вид он определяет по содержимому, issue #1265), так что исход один на любом стенде: отказ.
  // Какой именно — зависит от стенда (движок не настроен либо не справился), поэтому сверяются слова,
  // общие для любого отказа. Черновик при этом обязан завестись и открыться: распознавание — помощь,
  // а не условие. И о неудаче говорят все три места — форма, строка списка и отбор.
  await check('счёт из скана: отказ распознавания назван в форме, в строке и стоит под отбором', async () => {
    const fileName = `Скан-${stamp}.pdf`;
    await page.locator('input[type=file][accept="application/pdf,image/png,image/jpeg"]').setInputFiles({
      name: fileName, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${stamp}`),
    });

    // Черновик открылся сам: в адресе назван счёт, скан приложен.
    await named('черновик из скана не открылся', () => page.waitForURL(/[?&]invoice=/, { timeout: 15_000 }));
    await named('в форме отказ распознавания не назван',
      () => page.getByText(/Скан не распознан: /).waitFor({ timeout: 30_000 }));

    // Строка списка: номера нет — стоит имя файла, под ним причина.
    const row = page.locator('button', { hasText: fileName });
    await named('в строке списка отказ не назван',
      () => row.getByText(/не распознан — /).waitFor({ timeout: 15_000 }));

    // Отбор «Не распознано» — тот же счёт стоит под ним.
    await page.getByRole('button', { name: /^Не распознано/ }).click();
    await named('под отбором «Не распознано» счёта нет', () => row.waitFor({ timeout: 10_000 }));
    await page.getByRole('button', { name: /^Не распознано/ }).click();
  });

  // ── 8. Несколько сканов разом: по черновику на файл, непринятый НАЗВАН (issue #1093) ────────────
  //
  // Сторож задачи D4. Три файла: два годных по виду и один не того вида. Экран при этом не двигается
  // (открыт остаётся счёт из проверки 7), а о результате говорит полоса над списком — и она не
  // исчезает сама: непринятый файл с причиной обязан дожить до взгляда человека.
  await check('пакет сканов: по черновику на файл, непринятый назван, экран не сдвинулся', async () => {
    const opened = new URL(page.url()).searchParams.get('invoice');
    const names = [`Пакет-${stamp}-2.pdf`, `Пакет-${stamp}-10.pdf`];
    await page.locator('input[type=file][accept="application/pdf,image/png,image/jpeg"]').setInputFiles([
      ...names.map(name => ({ name, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${name}`) })),
      { name: `Заметки-${stamp}.txt`, mimeType: 'text/plain', buffer: Buffer.from('не скан') },
    ]);

    const bar = page.getByRole('navigation', { name: 'Счета на оплату' }).getByRole('status')
      .filter({ hasText: 'Заведено 2 из 3' });
    await named('итог пакета не показан', () => bar.waitFor({ timeout: 30_000 }));
    await named('непринятый файл не назван с причиной',
      () => bar.locator('li', { hasText: `Заметки-${stamp}.txt` }).getByText('не PDF, PNG или JPEG').waitFor({ timeout: 5_000 }));
    for (const name of names)
      await named(`черновика «${name}» в списке нет`,
        () => page.locator('button', { hasText: name }).waitFor({ timeout: 15_000 }));
    if (new URL(page.url()).searchParams.get('invoice') !== opened)
      throw new Error('пакет из нескольких файлов сменил открытый счёт');
  });
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Форма счёта');
