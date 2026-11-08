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

import { readFileSync } from 'node:fs';
import {
  BASE, launchBrowser, login, createChecks, named, settled, watchRequests, expandInvoiceParts, invoiceMenu, until,
} from './harness.mjs';
import { invoiceLayoutChecks } from './invoice-layout-checks.mjs';
import { invoiceScreenLayoutChecks } from './invoice-screen-layout-checks.mjs';
import { viewerShowsPdf } from './pdf-viewer-checks.mjs';
import { invoiceScanChecks } from './invoice-scan-checks.mjs';
import { invoiceUndoChecks } from './invoice-undo-checks.mjs';
import { statusAction, statusBanner, statusOpen, untilStatus } from './invoice-status.mjs';

/**
 * Отказ о САМОМ файле (issue #1343, #1344): начало полосы в форме и строка списка. Источник слов —
 * `src/features/costs/recognition.ts` (`failureNote` и `shortReason` для `BadFile`): меняются вместе.
 */
const BAD_FILE = { note: 'Файл не распознан: файл не читается', row: 'не распознан — файл не читается' };

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, skip, summarize } = createChecks();
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
 * Открыть счёт списка по номеру и дождаться формы.
 *
 * Кнопка «Сохранить» — одна, в сводке (costs-invoice-screen-10-layout, D-10.7): она пишет и шапку, и
 * строки. Заполненные части открываются свёрнутыми — проверки этого прогона не про свёртку и раскрывают их.
 */
async function open(number) {
  await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
  await page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 10_000 });
  await expandInvoiceParts(page);
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

/** Сколько меток видно на форме: знаков «со скана» в хвосте полей ([UI-88], 11 D-11.4) — у полей шапки нет ни рамки, ни
 *  строки подсказки. */
const markCount = () => page.locator('[data-field-scan-mark]').count();

/**
 * Приложить скан — многочастным запросом, тем же адресом, что и форма.
 *
 * Свой скан, а не посеянный: у посеянного счёта есть двойник с тем же номером и БЕЗ скана, и выбор
 * «первого по номеру» приводил бы то к одному, то к другому. Проверка, зависящая от порядка строк в
 * списке, краснеет через раз и выглядит поломкой формы.
 */
async function attachScan(id, base64 = PNG_BASE64, name = 'скан.png', type = 'image/png') {
  await page.evaluate(async ([id, base64, name, type]) => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const form = new FormData();
    form.append('file', new Blob([bytes], { type }), name);
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const seen = await fetch(`/api/costs/invoices/${id}`, { headers: { Authorization: `Bearer ${token}` } });
    if (!seen.ok) throw new Error(`счёт для файла не прочитан: ${seen.status}`);
    const res = await fetch(`/api/costs/invoices/${id}/scan`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'If-Match': `"${(await seen.json()).version}"` },
      body: form,
    });
    if (!res.ok) throw new Error(`файл не приложился: ${res.status} ${await res.text()}`);
  }, [id, base64, name, type]);
}

/**
 * Офисный файл для проверок образа — из `deploy/`: синтетическая книга-эталон и её двойник под
 * паролем лежат там ради сторожа конвертера (`deploy/converter.tests.sh`), второй копии не заводим.
 * Тип — «неизвестный»: так Excel называет браузер на машине без Office, и вид обязан определиться
 * по содержимому.
 */
const officeFile = name => readFileSync(new URL(`../../../deploy/${name}`, import.meta.url)).toString('base64');
const UNKNOWN_TYPE = 'application/octet-stream';

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

  // ── 4. Скан рядом с формой, а без места рядом «Файл» называет причину — в `invoice-scan-checks`. ──
  // Скан на своём просмотрщике (issue #1491): место рядом, фото, поддельный PDF, узкий экран — своим модулем.
  await invoiceScanChecks(page, check, { api, requisites, open, attachScan, named, officeFile, stamp });

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

  // ── 5а. Сумма, набранная с запятой и пробелом, сохраняется ─────────────────
  //
  // Поле присылает текст человека как есть («1 234,50»), а охрана записи ядра судила вид значения до
  // разбора и строку в числовом поле отвергала: сумму к оплате форма не сохраняла вовсе. Проверяется
  // ответом сервера и чтением — не видом поля, где набранный текст стоит и без сохранения.
  await check('сумма к оплате, набранная «1 234,50», сохраняется числом', async () => {
    const number = `СЧ-С${stamp}-3`;
    // Идентификатор — из ответа создания, а не поиском в списке: на стенде с накопленными счетами новый
    // в первую страницу списка может не попасть.
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'Сумма с запятой') });

    await open(number);
    await page.getByLabel('Сумма к оплате').fill('1 234,50');
    await page.getByLabel('В том числе НДС').fill('205,75');
    await save();

    const saved = (await api('GET', `/costs/invoices/${created.id}`)).requisites;
    if (saved['Итого'] !== 1234.5) throw new Error(`сумма к оплате легла как ${saved['Итого']}, ожидалось 1234.5`);
    if (saved['ВТомЧислеНДС'] !== 205.75) throw new Error(`НДС лёг как ${saved['ВТомЧислеНДС']}, ожидалось 205.75`);
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
    await expandInvoiceParts(page);
    await untilStatus(page, items => items.some(i => i.key === 'duplicate'), 'перечень не назвал дубликат');

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
    await expandInvoiceParts(page);

    const saved = await page.getByLabel('Назначение').inputValue();
    if (saved !== 'Правка при дубликате')
      throw new Error(`правка при дубликате не сохранилась: в поле «${saved}»`);

    // Строка пункта ведёт на ТОТ счёт — узнаём его по назначению, оно у счетов разное.
    await statusOpen(page, 'duplicate');
    // «Назначение» — в части «Ещё», а она открывается свёрнутой.
    await page.waitForTimeout(500);
    await expandInvoiceParts(page);
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
  // принял бы вовсе: вид он определяет по содержимому, issue #1265), и исход один на любом стенде: отказ
  // о САМОМ файле. Сервер проверяет файл раньше готовности движков (#1344), поэтому ни «не настроено»
  // (стенд без движка, CI), ни «недоступно» (движок отказал) здесь быть не может — а файл к движку не
  // уходит вовсе. Черновик при этом обязан завестись и открыться: распознавание — помощь, а не условие.
  // И о неудаче говорят все три места — форма, строка списка и отбор.
  await check('счёт из скана: отказ о файле назван в форме, в строке и стоит под отбором', async () => {
    const fileName = `Скан-${stamp}.pdf`;
    // Выбор файла сужен реестром видов (issue #1266): пустой `accept` — реестр не дошёл до экрана,
    // и тогда выбор предлагает что угодно. Перечень сверяется по признаку, а не слово в слово: его
    // состав — дело сервера.
    await page.locator('input[type=file][multiple][accept*=".pdf"]').setInputFiles({
      name: fileName, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${stamp}`),
    });

    // Черновик открылся сам: в адресе назван счёт, скан приложен.
    await named('черновик из скана не открылся', () => page.waitForURL(/[?&]invoice=/, { timeout: 15_000 }));
    const note = page.getByText(/^Файл не распознан: /);
    await named('в форме отказ распознавания не назван', () => note.waitFor({ timeout: 30_000 }));
    const said = await note.innerText();
    if (!said.startsWith(BAD_FILE.note) || /не настроено|недоступно/.test(said))
      throw new Error(`в форме отказ не о файле: «${said}»`);

    // Строка списка: номера нет — стоит имя файла, под ним причина. У черновика со сбоем файл открыт рядом,
    // и списка тогда нет (costs-invoice-screen-10-layout, D-10.3): «Файл» закрывает панель — список встаёт.
    const file = page.getByTestId('invoice-header').getByRole('button', { name: 'Файл', exact: true });
    await file.click();
    const row = page.locator('button', { hasText: fileName });
    await named('в строке списка отказ о файле не назван',
      () => row.getByText(BAD_FILE.row).waitFor({ timeout: 15_000 }));

    // Отбор «Не распознано» — тот же счёт стоит под ним.
    await page.getByRole('button', { name: /^Не распознано/ }).click();
    await named('под отбором «Не распознано» счёта нет', () => row.waitFor({ timeout: 10_000 }));
    await page.getByRole('button', { name: /^Не распознано/ }).click();
    // Файл остаётся закрытым: проверки загрузки ниже смотрят список. Выбор возвращает проверка 8а.
  });

  // ── 8. Несколько сканов разом: по черновику на файл, непринятый НАЗВАН (issue #1093) ────────────
  //
  // Сторож задачи D4. Три файла: два годных по виду и один не того вида. Экран при этом не двигается
  // (открыт остаётся счёт из проверки 7), а о результате говорит полоса над списком — и она не
  // исчезает сама: непринятый файл с причиной обязан дожить до взгляда человека.
  //
  // ⚠️ Содержимое нарочно нечитаемо: исход `BadFile` без запроса к движку (#1344). Настоящий PDF здесь
  // ушёл бы движку на стенде, где он настроен, — платным запросом за каждый прогон.
  await check('пакет сканов: по черновику на файл, непринятый назван, экран не сдвинулся', async () => {
    const opened = new URL(page.url()).searchParams.get('invoice');
    const names = [`Пакет-${stamp}-2.pdf`, `Пакет-${stamp}-10.pdf`];
    await page.locator('input[type=file][multiple][accept*=".pdf"]').setInputFiles([
      ...names.map(name => ({ name, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${name}`) })),
      { name: `Заметки-${stamp}.txt`, mimeType: 'text/plain', buffer: Buffer.from('не скан') },
    ]);

    const bar = page.getByRole('navigation', { name: 'Счета на оплату' }).getByRole('status')
      .filter({ hasText: 'Заведено 2 из 3' });
    await named('итог пакета не показан', () => bar.waitFor({ timeout: 30_000 }));
    await named('непринятый файл не назван с причиной',
      () => bar.locator('li', { hasText: `Заметки-${stamp}.txt` }).getByText('не PDF, PNG, JPEG, Excel или Word').waitFor({ timeout: 5_000 }));
    for (const name of names)
      await named(`черновика «${name}» в списке нет`,
        () => page.locator('button', { hasText: name }).waitFor({ timeout: 15_000 }));
    if (new URL(page.url()).searchParams.get('invoice') !== opened)
      throw new Error('пакет из нескольких файлов сменил открытый счёт');

    // Отбор загрузки (issue #1294): список отсортирован по дате счёта, и после распознавания её
    // черновики разъезжаются между старыми счетами. Из полосы одним действием видны ровно они.
    const rows = page.getByTestId('invoice-row');
    const line = page.getByTestId('upload-line');
    await page.getByPlaceholder('Номер, поставщик, назначение…').fill('заведомо ничего');
    await line.getByRole('button', { name: 'Показать только их' }).click();
    await named('полоса не назвала число счетов под отбором загрузки',
      () => line.getByText('В списке только они: 2.').waitFor({ timeout: 10_000 }));
    // Третья строка — открытый счёт из проверки 7: заведён не этой загрузкой, но остаётся строкой,
    // пока открыт. В число полосы он не входит.
    const shown = await rows.allInnerTexts();
    if (shown.length !== 3 || !names.every(name => shown.some(text => text.includes(name))))
      throw new Error(`под отбором загрузки не её счета и открытый: ${JSON.stringify(shown)}`);
    // Поиск сужает внутри отбора, и число в полосе идёт за строками.
    await page.getByPlaceholder('Номер, поставщик, назначение…').fill(names[0]);
    await named('число в полосе не пошло за поиском',
      () => line.getByText('В списке только они: 1.').waitFor({ timeout: 10_000 }));
    await page.getByPlaceholder('Номер, поставщик, назначение…').fill('');
    await line.getByText('В списке только они: 2.').waitFor({ timeout: 10_000 });
    if (await page.getByPlaceholder('Номер, поставщик, назначение…').inputValue() !== '')
      throw new Error('отбор загрузки не снял поиск — под ним её счетов не видно');
    // Чип очереди отбор снимает: пересечение дало бы пустой список без объяснения.
    await page.getByRole('button', { name: /^Разобрать/ }).click();
    await named('чип очереди не снял отбор загрузки',
      () => line.getByRole('button', { name: 'Показать только их' }).waitFor({ timeout: 10_000 }));
    await page.getByRole('button', { name: /^Разобрать/ }).click();
    // И обратно — «Показать все счета» возвращает общий список.
    await line.getByRole('button', { name: 'Показать только их' }).click();
    await line.getByRole('button', { name: 'Показать все счета' }).click();
    await named('«Показать все счета» не вернуло общий список',
      () => page.waitForFunction(() => document.querySelectorAll('[data-testid="invoice-row"]').length > 3, null, { timeout: 10_000 }));
  });

  // ── 8а. Тот же файл дважды: оговорка по СОДЕРЖИМОМУ, черновик всё равно заведён (issue #1295) ────
  //
  // Сторож задачи. Три файла: два несут содержимое файла из проверки 8 под другими именами, третий
  // — имя того файла с другим содержимым. Повторами названы первые два; третий — нет: сравнение по
  // имени вместо содержимого перевернуло бы оба ответа.
  //
  // ⚠️ Содержимое нарочно нечитаемо: исход `BadFile` без запроса к движку (#1344) — см. проверку 8.
  await check('тот же файл дважды: повтор назван в полосе и на форме, одноимённый с другим содержимым — нет', async () => {
    const before = `Пакет-${stamp}-2.pdf`;
    const repeats = [`Повтор-${stamp}-1.pdf`, `Повтор-${stamp}-2.pdf`];
    await page.locator('input[type=file][multiple][accept*=".pdf"]').setInputFiles([
      ...repeats.map(name => ({ name, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 не настоящий ${before}`) })),
      { name: before, mimeType: 'application/pdf', buffer: Buffer.from(`%PDF-1.4 другое содержимое ${stamp}`) },
    ]);

    const list = page.getByTestId('upload-repeats');
    await named('полоса не назвала повторы',
      () => list.getByText('Уже были приложены — черновики всё равно заведены (2):').waitFor({ timeout: 30_000 }));
    const lines = await list.locator('li').allInnerTexts();
    if (lines.length !== 2 || !repeats.every(name => lines.some(text => text.startsWith(name))))
      throw new Error(`повторами названы не те файлы: ${JSON.stringify(lines)}`);
    // Прежний счёт без номера назван именем файла и временем заведения до секунд: порознь они
    // двух черновиков одной загрузки не различают.
    if (!lines.every(text => /уже приложен к счёту без номера, «.+», заведён \d\d\.\d\d\.\d{4}, \d\d:\d\d:\d\d/.test(text)))
      throw new Error(`прежний счёт назван не временем заведения: ${JSON.stringify(lines)}`);

    // Из полосы — на новый черновик: оговорка стоит на форме и ведёт к удалению лишнего.
    await list.getByRole('button', { name: repeats[0] }).click();
    await untilStatus(page, items => items.some(i => i.key === 'same-file' && /приложен ещё к \d+ счетам: /.test(i.text)),
      'на форме оговорки о том же файле нет', 15_000);
    // Черновик заведён этой вкладкой и не правлен — удаляется без окна, с «Вернуть» (13, D-13.5).
    await statusAction(page, 'same-file', 'Удалить черновик');
    if (await page.getByRole('dialog').count()) throw new Error('удаление нового черновика-повтора открыло окно');
    const undo = page.locator('[role=region][aria-label^="Notifications"] li').filter({ hasText: 'Черновик удалён' }).getByRole('button', { name: 'Вернуть' });
    await named('снэкбара «Черновик удалён · Вернуть» нет', () => undo.waitFor({ timeout: 5_000 }));
    await undo.click();
    await untilStatus(page, items => items.some(i => i.key === 'same-file'), '«Вернуть» не вернул черновик-повтор', 10_000);
  });
  // Файл, закрытый проверкой 7, — снова «открыт», как без выбора: следующие проверки смотрят панель файла.
  await api('PUT', '/account/settings', { invoiceFile: 'open' });
  await page.evaluate(() => localStorage.setItem('crg-invoice-file', 'open'));

  // ── 9. Счёт в Excel: рядом с формой — вид для чтения, а документ — оригинал (issue #1270) ───────
  //
  // Сторож задачи, на НАСТОЯЩЕМ конвертере: серверные тесты ходят к подставному, и только здесь
  // книга в самом деле превращается в PDF. Распознавание запускается кнопкой; чем оно кончится,
  // зависит от стенда (движок настроен или нет), поэтому сверяется одно: отказ, если он есть, — НЕ
  // об образе. Образ построен, и причина «файл не приведён к читаемому виду» здесь была бы поломкой.
  //
  // ⚠️ Образ книги — ИСПРАВНЫЙ PDF, и на стенде с настроенным облачным движком нажатие ушло бы к нему
  // платным запросом (#1344). Там запуск не проверяется — пропуск назван строкой прогона; на стенде без
  // облачного движка (CI) проверка прежняя. Остальное — вид для чтения, оригинал, панель — на любом стенде.
  // Ollama в счёт не идёт: он свой, а в CI включён по умолчанию и «настроен», хотя не запущен, — по нему
  // проверка пропускалась бы как раз там, где обязана идти. Имя — без регистра, как у сервера
  // (`EngineReadiness.IsOllama`). Настройки не прочитаны — провал своей проверкой, и нажатия нет:
  // неизвестно, не уйдёт ли оно к платному движку.
  let engines = null;
  await check('настройки распознавания прочитаны', async () => {
    engines = Object.entries((await api('GET', '/settings/integrations')).recognition ?? {})
      .filter(([name, engine]) => name.toLowerCase() !== 'ollama' && engine.enabled && !engine.missing)
      .map(([name]) => name);
  });
  const press = engines?.length === 0;
  if (!press)
    skip('счёт в Excel: распознавание запускается', engines
      ? `движок настроен (${engines.join(', ')}) — запуск не проверяется, прогон к движку не ходит (#1344)`
      : 'настройки распознавания не прочитаны — запуск не проверяется (#1344)');
  await check('счёт в Excel: показан вид для чтения с оговоркой, оригинал скачивается'
    + (press ? ', распознавание запускается' : ''), async () => {
    const number = `СЧ-X${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'В Excel') });
    const book = officeFile('converter-reference.xlsx');
    await attachScan(created.id, book, 'Счёт.xlsx', UNKNOWN_TYPE);

    await open(number);
    const caveat = page.getByTestId('image-caveat');
    await named('строки «Вид для чтения» в панели нет', () => caveat.waitFor({ timeout: 60_000 }));
    await named('панель не говорит, что документ — оригинал',
      () => caveat.getByText('документ — оригинал').waitFor({ timeout: 5_000 }));
    await viewerShowsPdf(page, named, caveat);

    // Скачивается оригинал — та же книга, байт в байт, а не PDF.
    const [download] = await Promise.all([
      page.waitForEvent('download', { timeout: 15_000 }),
      page.getByRole('button', { name: 'Оригинал', exact: true }).click(),
    ]);
    const saved = readFileSync(await download.path()).toString('base64');
    if (saved !== book) throw new Error('кнопка «Оригинал» отдала не приложенный файл');
    if (!download.suggestedFilename().endsWith('.xlsx'))
      throw new Error(`оригинал сохраняется под именем «${download.suggestedFilename()}», а не как Excel`);

    if (press) {
      // «Распознать файл» — пункт «⋮» сводки (D-10.6).
      await invoiceMenu(page, 'Распознать файл');
      // Исход — любой, кроме «идёт»: распознавание кончилось, а отказ об образе не назван.
      await named('распознавание не дало исхода', () => until(async () => {
        const state = (await api('GET', `/costs/invoices/${created.id}/recognition`)).state;
        return state === 'done' || state === 'failed';
      }, { timeout: 60_000, every: 500 }));
      await page.waitForTimeout(2500);
      if ((await page.getByText('не приведён к читаемому виду').count()) !== 0)
        throw new Error('распознавание отказало из-за образа, хотя образ построен');
    }
    // Панель осталась НА ЭКРАНЕ: таблица строк, пришедших из распознавания, шире формы и однажды
    // выталкивала панель за правый край — вид был построен, а видеть его было нельзя.
    const box = await caveat.boundingBox();
    if (!box || box.x + box.width > 1500 + 1)
      throw new Error('панель файла уехала за правый край экрана');
  });

  // Раскладка вида по профилю печати (issue #1456) — своим модулем, с помощниками этого прогона.
  await invoiceLayoutChecks(page, check, { api, requisites, open, attachScan, named, officeFile, UNKNOWN_TYPE, stamp });
  await invoiceUndoChecks(page, check, { named, stamp, BASE });
  // Раскладка экрана по варианту Г (costs-invoice-screen-10-layout) — на четырёх размерах ноутбука.
  await invoiceScreenLayoutChecks(page, check, { api, requisites, attachScan, named, stamp, BASE });

  // ── 10. Файл под паролем ПРИЛОЖЕН, а причина названа словами (issue #1270) ──────────────────────
  //
  // Отказ построения — не отказ загрузки: загрузка выше не бросила, счёт открывается, оригинал
  // скачивается. А распознать нельзя — и причину (пароль) говорят оба места: панель и шапка формы.
  await check('файл под паролем приложен и скачивается, а распознать нельзя — причина названа', async () => {
    const number = `СЧ-P${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'Под паролем') });
    const locked = officeFile('converter-protected.xlsx');
    await attachScan(created.id, locked, 'Закрытый.xlsx', UNKNOWN_TYPE);

    await open(number);
    const refused = page.getByTestId('image-refused');
    await named('панель не говорит, что вид не построен', () => refused.waitFor({ timeout: 60_000 }));
    await named('в панели причина (пароль) не названа', () => refused.getByText(/парол/).waitFor({ timeout: 5_000 }));
    // «Распознать файл» — пункт «⋮» сводки: у файла под паролем он неактивен и называет причину (D-10.6).
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Ещё' }).click();
    await named('у формы причина (пароль) не названа',
      () => page.getByRole('menu').getByText(/Не распознаётся: .*парол/).waitFor({ timeout: 10_000 }));
    const start = page.getByRole('menuitem', { name: /Распознать файл/ });
    if (await start.getAttribute('aria-disabled') !== 'true' && await start.getAttribute('data-disabled') === null)
      throw new Error('у файла под паролем «Распознать файл» доступно — оно ответило бы отказом');
    await page.keyboard.press('Escape');

    const [download] = await Promise.all([
      page.waitForEvent('download', { timeout: 15_000 }),
      refused.getByRole('button', { name: 'Скачать оригинал' }).click(),
    ]);
    if (readFileSync(await download.path()).toString('base64') !== locked)
      throw new Error('«Скачать оригинал» отдала не приложенный файл');
  });

  // ── 11. «Отклонить»: счёт остаётся в реестре, из очереди разбора уходит, возвращается кнопкой под строками ──
  await check('счёт отклоняется из меню «Ещё», остаётся в реестре и возвращается в черновик', async () => {
    const number = `ОТКЛ-${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'отклонение') });
    const seen = (await api('GET', `/costs/invoices/${created.id}`)).version;
    // Строка без позиции ставит счёт в очередь «Разобрать» — из неё отклонённый обязан уйти.
    await page.evaluate(async ([id, seen]) => {
      const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
      const res = await fetch(`/api/costs/invoices/${id}/lines`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, 'If-Match': seen },
        body: JSON.stringify({ lines: [{ id: null, nomenclature: null, supplierText: 'позиции ещё нет', quantity: 1, price: 10, vatRate: null }] }),
      });
      if (!res.ok) throw new Error(`строки не записаны: ${res.status} ${(await res.text()).slice(0, 200)}`);
    }, [created.id, seen]);
    const queued = async () => (await api('GET', '/costs/invoices?needsParsing=true')).some(i => i.id === created.id);
    if (!await queued()) throw new Error('черновик со строкой без позиции не встал в очередь «Разобрать»');

    await open(number);
    // Без окна (13, D-13.1): пояснение — строкой под пунктом, после ответа — «Вернуть».
    const reject = async () => {
      await page.getByRole('button', { name: 'Ещё', exact: true }).click();
      const item = page.getByRole('menuitem', { name: /^Отклонить/ });
      await named('пункт не сказал, что счёт останется в реестре', () => item.getByText(/останется в реестре/).waitFor({ timeout: 10_000 }));
      await Promise.all([
        page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${created.id}/rejected`) && r.ok()),
        item.click(),
      ]);
      if (await page.getByRole('dialog').count()) throw new Error('«Отклонить» открыло окно');
      const said = page.locator('[role=region][aria-label^="Notifications"] li').filter({ hasText: `Счёт № ${number} отклонён.` });
      await named('снэкбара «Счёт № … отклонён · Вернуть» нет', () => said.getByRole('button', { name: 'Вернуть' }).waitFor({ timeout: 5_000 }));
      return said;
    };
    // «Вернуть» снэкбара — обратный переход: счёт снова черновик.
    await Promise.all([
      page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${created.id}/draft`) && r.ok()),
      (await reject()).getByRole('button', { name: 'Вернуть' }).click(),
    ]);
    await named('«Вернуть» не вернул счёт в черновик',
      () => page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 }));
    await reject();
    await named('чип состояния не стал «Отклонён»',
      () => page.getByText('Отклонён', { exact: true }).first().waitFor({ timeout: 10_000 }));

    // В реестре счёт есть — с состоянием; из очереди разбора ушёл; «Разобран» под строками не предлагается.
    const listed = (await api('GET', '/costs/invoices')).find(i => i.id === created.id);
    if (listed?.state !== 'Отклонён') throw new Error(`в реестре у счёта состояние «${listed?.state}», а не «Отклонён»`);
    if (await queued()) throw new Error('отклонённый счёт остался в очереди «Разобрать»');
    if (await page.getByRole('button', { name: 'Разобран', exact: true }).count())
      throw new Error('у отклонённого счёта предложено «Разобран» — сервер на это отвечает отказом');
    if (await page.getByRole('button', { name: 'Отметить оплату' }).count())
      throw new Error('у отклонённого счёта предложено «Отметить оплату» — сервер на это отвечает отказом');

    // У отклонённого в «⋮» нет «Отклонить» — а возврат стоит там же (D-10.6), одним входом.
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Ещё' }).click();
    if (await page.getByRole('menuitem', { name: /^Отклонить/ }).count())
      throw new Error('у отклонённого счёта в меню осталось «Отклонить»');
    await Promise.all([
      page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${created.id}/draft`) && r.ok()),
      page.getByRole('menuitem', { name: 'Вернуть в черновик' }).click(),
    ]);
    await named('чип состояния не вернулся к «Черновик»',
      () => page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 }));
    if (!await queued()) throw new Error('возвращённый в черновик счёт не вернулся в очередь «Разобрать»');
  });

  // ── 12. Удаление (issue #1282): черновик уходит из формы и из реестра; сосед видит «счёта больше нет» ──
  await check('черновик удаляется из меню «Ещё», а открытый у соседа счёт говорит, что его больше нет', async () => {
    const number = `УДАЛ-${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'заведён по ошибке') });

    await open(number);
    await page.getByRole('button', { name: 'Ещё', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Удалить счёт…' }).click();
    const dialog = page.getByRole('dialog');
    await named('диалог не сказал, что вернуть счёт будет нельзя',
      () => dialog.getByText('Корзины нет').waitFor({ timeout: 10_000 }));
    await Promise.all([
      page.waitForResponse(r => r.request().method() === 'DELETE' && r.url().endsWith(`/costs/invoices/${created.id}`) && r.status() === 204),
      dialog.getByRole('button', { name: `Удалить счёт № ${number}` }).click(),
    ]);
    await named('после удаления форма не закрылась', () => page.getByText('Выберите счёт').waitFor({ timeout: 10_000 }));
    if (page.url().includes('invoice=')) throw new Error(`удалённый счёт остался в адресе: ${page.url()}`);
    if ((await api('GET', '/costs/invoices')).some(i => i.id === created.id))
      throw new Error('удалённый счёт остался в реестре');
    await named('список слева всё ещё показывает удалённый счёт',
      () => page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).waitFor({ state: 'detached', timeout: 10_000 }));

    // Сосед: счёт открыт, в поле набрано — и тут счёт удаляют. Узнаёт он об этом на сохранении.
    const neighbour = `СОСЕД-${stamp}`;
    const other = await api('POST', '/costs/invoices', { requisites: requisites(neighbour, 'открыт у соседа') });
    await open(neighbour);
    await page.getByLabel('Основание').fill('набрано до удаления');
    await page.evaluate(async ([id, seen]) => {
      const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
      const res = await fetch(`/api/costs/invoices/${id}`, {
        method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': seen },
      });
      if (res.status !== 204) throw new Error(`счёт не удалён: ${res.status} ${(await res.text()).slice(0, 200)}`);
    }, [other.id, other.version]);
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await named('форма не сказала, что счёта больше нет',
      () => statusBanner(page).filter({ hasText: 'Счёт удалён' }).waitFor({ timeout: 10_000 }));
    if (await page.getByLabel('Основание').inputValue() !== 'набрано до удаления')
      throw new Error('набранное пропало вместе со счётом — а полоса обещает, что оно на экране');
    if (await page.getByRole('button', { name: 'Сохранить', exact: true }).count())
      throw new Error('у счёта, которого больше нет, осталась кнопка «Сохранить»');
    await page.getByRole('button', { name: 'Закрыть счёт' }).click();
    await named('«Закрыть счёт» форму не закрыла', () => page.getByText('Выберите счёт').waitFor({ timeout: 10_000 }));
  });

  // ── 13. Неудачное перечитывание (issue #1287): форма остаётся с набранным, отказ — полосой ──
  await check('неудачное перечитывание открытого счёта форму не снимает: набранное цело, отказ полосой', async () => {
    const number = `ОБРЫВ-${stamp}`;
    const created = await api('POST', '/costs/invoices', { requisites: requisites(number, 'перечитывание') });
    await open(number);
    await page.getByLabel('Основание').fill('набрано до обрыва');

    // Сохранение отвечает «изменили» — форма перечитывает счёт, а чтение падает.
    const address = `**/api/costs/invoices/${created.id}`;
    await page.route(address, route => route.request().method() === 'GET'
      ? route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"Сервер недоступен (проба)."}' })
      : route.fulfill({ status: 409, contentType: 'application/json', body: '{"error":"Счёт тем временем изменили (проба)."}' }));
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await named('полосы «не перечитался» нет — форма снята или отказ не показан',
      () => statusBanner(page).filter({ hasText: 'Счёт не перечитался' }).waitFor({ timeout: 15_000 }));
    if (await page.getByLabel('Основание').inputValue() !== 'набрано до обрыва')
      throw new Error('набранное пропало при неудачном перечитывании');

    await page.unroute(address);
    await statusBanner(page).filter({ hasText: 'Счёт не перечитался' }).getByRole('button', { name: 'Повторить' }).click();
    await named('после удачного повтора полоса осталась',
      () => statusBanner(page).filter({ hasText: 'Счёт не перечитался' }).waitFor({ state: 'detached', timeout: 10_000 }));
    if (await page.getByLabel('Основание').inputValue() !== 'набрано до обрыва')
      throw new Error('набранное пропало после удачного повтора');
  });

  // ── 14. Телефон (sharedui-md3-15, задача 2.2): плавающая кнопка стоит над нижней навигацией и не
  // закрывает конец списка — под последней строкой оставлено её место (`--fab-clearance`). Полоса
  // телефона стала 64, и место под список изменилось. Своё окно с тем же входом: у открытой формы
  // выше набраны правки, и уход со страницы спросил бы о них; новый вход занял бы место в счёте входов
  // прогона (`suites.test.mjs`) — токен переносится.
  await check('на телефоне плавающая кнопка над нижней навигацией и не закрывает последнюю строку', async () => {
    const token = await page.evaluate(() => localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token'));
    const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'ru-RU' });
    await ctx.addInitScript(t => localStorage.setItem('access_token', t), token);
    const phone = await ctx.newPage();
    try {
      await watchRequests(phone);
      await phone.goto(`${BASE}/invoices`);
      await settled(phone);
      await named('плавающей кнопки на телефоне нет', () => phone.getByTestId('primary-fab').waitFor({ timeout: 10_000 }));
      await phone.getByTestId('invoice-row').first().waitFor({ timeout: 10_000 });
      // До конца прокрутки — повторами: список догружается по мере прокрутки.
      let rows = 0;
      for (let i = 0; i < 20; i++) {
        const now = await phone.evaluate(() => {
          const all = document.querySelectorAll('[data-testid=invoice-row]');
          let box = all[all.length - 1].parentElement;
          while (box && !(box.scrollHeight > box.clientHeight && /auto|scroll/.test(getComputedStyle(box).overflowY))) box = box.parentElement;
          if (box) box.scrollTop = box.scrollHeight;
          return all.length;
        });
        await settled(phone);
        if (now === rows) break;
        rows = now;
      }
      const m = await phone.evaluate(() => {
        const all = document.querySelectorAll('[data-testid=invoice-row]');
        const box = el => document.querySelector(el)?.getBoundingClientRect();
        return { last: all[all.length - 1].getBoundingClientRect().bottom, fab: box('[data-testid=primary-fab]'), nav: box('#app-bottom-nav') };
      });
      if (!m.nav) throw new Error('на 390 px нет нижней навигации');
      if (m.fab.bottom > m.nav.top + 0.5) throw new Error(`плавающая кнопка (низ ${Math.round(m.fab.bottom)}) заходит на нижнюю навигацию (верх ${Math.round(m.nav.top)})`);
      if (m.last > m.fab.top + 0.5) throw new Error(`последняя строка списка (низ ${Math.round(m.last)}) уходит под плавающую кнопку (верх ${Math.round(m.fab.top)})`);
    } finally {
      await ctx.close();
    }
  });
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Форма счёта');
