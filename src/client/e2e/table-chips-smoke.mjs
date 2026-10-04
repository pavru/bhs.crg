// Живой прогон ЧИПОВ ОТБОРА над таблицей модуля (задача G1d этапа 2, issue #1091, ТЗ CORE-33).
//
// ЗАЧЕМ ОН ЕСТЬ. Юнит-тесты проверяют правила чипов без экрана: что чип говорит и каким деревом он
// становится. Но два обещания задачи — про экран целиком, и выдумать их в юнит-тесте нельзя:
//   • «чип по колонке-перечислению не принимает произвольную строку» — значит, на экране у такой
//     колонки НЕТ поля, в которое строку можно набрать;
//   • «снятие чипа меняет запрос, а не только вид» — значит, после крестика к серверу уходит другой
//     запрос и приходят другие строки. Чип, исчезнувший с экрана при прежней выдаче, выглядел бы так
//     же — и был бы худшим из отказов: отбор действует, а его не видно.
//
// ⚠️ ДАННЫЕ ПРОГОН ГОТОВИТ СЕБЕ САМ: счёт с уникальным номером. Отбор по этому номеру обязан найти
// ровно его — на любой базе, сколько бы счетов в ней ни накопилось.
//
// ⚠️ Элементы ищутся ПО ВИДИМОМУ ТЕКСТУ и ролям, без служебных атрибутов.
//
// Требует поднятых фронта и бэка, посеянных данных и ВКЛЮЧЁННОГО модуля `costs`
// (`Modules__Enabled=id,costs`) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/table-chips-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-7);
const number = `ЧИП-${stamp}`;

/** Запросы к таблице в порядке отправки: по ним и судим, изменился ли сам отбор. */
const requests = [];
page.on('request', r => { if (r.url().includes('/api/tables/costs.invoices')) requests.push(decodeURIComponent(r.url())); });
const lastRequest = () => requests[requests.length - 1] ?? '';

await login(page);

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

try {
  await api('POST', '/costs/invoices', { requisites: { 'Номер': number, 'Назначение': 'Чипы отбора' } });
} catch (e) {
  console.error(`Счёт для прогона не завёлся: ${e.message}. Включён ли модуль costs у сервера `
    + '(Modules__Enabled=id,costs)? Без счёта проверять нечего, и зелёный прогон был бы отчётом о '
    + 'работе, которой не было.');
  await browser.close();
  process.exit(1);
}

await page.goto(`${BASE}/tables/costs.invoices`, { waitUntil: 'networkidle' });

const chips = page.getByRole('group', { name: 'Условия отбора' });
const addChip = chips.getByRole('button', { name: 'условие', exact: true });
const editor = page.getByRole('dialog');
/** Строки таблицы с данными — без строки состояния «Отбор ничего не нашёл». */
const dataRows = () => page.locator('tbody tr').filter({ hasNot: page.locator('td[colspan]') });

/**
 * Дождаться, пока в таблице станет ровно столько строк. Именно ждать: пока идёт новый запрос, на
 * экране остаются прежние строки (приглушённые), и подсчёт «сразу после щелчка» считал бы их.
 */
async function rowsBecome(count, what) {
  try {
    await page.waitForFunction(
      n => [...document.querySelectorAll('tbody tr')].filter(r => !r.querySelector('td[colspan]')).length === n,
      count, { timeout: 8000 });
  } catch {
    throw new Error(`${what}: строк ${await dataRows().count()}, а обязано быть ${count}`);
  }
}

/** Дождаться, пока к таблице уйдёт запрос сверх уже отправленных. */
async function requestAfter(sent) {
  const deadline = Date.now() + 8000;
  while (requests.length === sent && Date.now() < deadline) await page.waitForTimeout(100);
  await page.waitForLoadState('networkidle');
}

try {
  await check('таблица счетов открыта, и над ней есть чем добавить условие', async () => {
    await page.getByRole('heading', { name: 'Счета на оплату' }).waitFor({ timeout: 10000 });
    await addChip.waitFor();
  });

  await check('у колонки-выбора значение — список её значений, поля для строки нет', async () => {
    await addChip.click();
    await editor.getByLabel('Колонка').selectOption({ label: 'Состояние оплаты' });

    const value = editor.getByLabel('Значение');
    const tag = await value.evaluate(el => el.tagName);
    if (tag !== 'SELECT') throw new Error(`значение вводится элементом ${tag}, а не выбором из списка`);
    const options = await value.locator('option').allTextContents();
    const want = ['— значение —', 'Не оплачен', 'Оплачен'];
    if (JSON.stringify(options) !== JSON.stringify(want))
      throw new Error(`в списке ${JSON.stringify(options)}, а перечень колонки — ${JSON.stringify(want.slice(1))}`);
    // Ни одного поля, куда строку можно набрать, в окошке условия нет.
    const typed = await editor.locator('input:not([type=checkbox])').count();
    if (typed !== 0) throw new Error(`в окошке условия есть поле для набора строки (${typed})`);

    // «Содержит» у выбора не предлагается: это был бы набор строки под другим именем.
    const operators = await editor.getByLabel('Оператор').locator('option').allTextContents();
    if (operators.some(o => o.includes('содержит')))
      throw new Error(`выбору предложен оператор текста: ${JSON.stringify(operators)}`);
    await page.keyboard.press('Escape');
  });

  await check('чип называет колонку, оператор и значение — и отбирает по ним на сервере', async () => {
    await addChip.click();
    await editor.getByLabel('Колонка').selectOption({ label: 'Номер счёта' });
    await editor.getByLabel('Оператор').selectOption({ label: 'содержит' });
    await editor.getByLabel('Значение').fill(number);
    await editor.getByRole('button', { name: 'Добавить' }).click();

    await chips.getByRole('button', { name: `Номер счёта содержит «${number}»`, exact: true }).waitFor();
    await rowsBecome(1, 'отбор по своему номеру');
    if (!lastRequest().includes(number)) throw new Error('условие чипа в запрос к таблице не попало');
  });

  await check('второй чип сужает отбор; пустая выдача названа отбором, а чипы остаются', async () => {
    await addChip.click();
    await editor.getByLabel('Колонка').selectOption({ label: 'Состояние оплаты' });
    await editor.getByLabel('Значение').selectOption({ label: 'Оплачен' });
    await editor.getByRole('button', { name: 'Добавить' }).click();

    await chips.getByRole('button', { name: 'Состояние оплаты: Оплачен', exact: true }).waitFor();
    // Только что заведённый счёт не оплачен — под вторым условием не остаётся ничего.
    await rowsBecome(0, 'под отбором «Оплачен»');
    await page.getByText('Отбор ничего не нашёл').waitFor({ timeout: 5000 });
    if ((await chips.getByRole('button', { name: /^Снять условие/ }).count()) !== 2)
      throw new Error('при пустой выдаче чипы пропали — объяснить пустоту стало нечем');
  });

  await check('снятие чипа меняет запрос, а не только вид', async () => {
    const before = requests.length;
    await chips.getByRole('button', { name: 'Снять условие «Состояние оплаты: Оплачен»' }).click();

    // Строки возвращаются — значит, ответ пришёл уже на другой отбор, а не чип пропал с экрана.
    await rowsBecome(1, 'после снятия второго чипа');
    // Запрос на прежний отбор мог и не уйти заново (ответ на него уже есть) — судим по строкам, а
    // по запросам проверяем обратное: НОВОГО запроса со снятым условием не было.
    if (requests.slice(before).some(r => r.includes('СостояниеОплаты')))
      throw new Error('снятое условие осталось в запросе: чип исчез, а отбор действует');
    if ((await chips.getByRole('button', { name: /^Снять условие/ }).count()) !== 1)
      throw new Error('вместе со снятым чипом пропал соседний');
  });

  await check('снят последний чип — отбора в запросе нет вовсе', async () => {
    const before = requests.length;
    await chips.getByRole('button', { name: /^Снять условие/ }).click();

    await page.waitForFunction(
      () => [...document.querySelectorAll('tbody tr')].filter(r => !r.querySelector('td[colspan]')).length > 1,
      null, { timeout: 8000 }).catch(() => { throw new Error('без отбора таблица не вернула остальные счета'); });
    if (requests.slice(before).some(r => r.includes('filter=')))
      throw new Error('после снятия последнего чипа ушёл запрос с отбором');
    if ((await chips.getByRole('button', { name: /^Снять условие/ }).count()) !== 0)
      throw new Error('чип остался на экране');
  });

  await check('отбор с «ИЛИ» назван сложным и чипами не подменён', async () => {
    await chips.getByRole('button', { name: 'Расширенный' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Условие', exact: true }).click();
    await dialog.getByRole('button', { name: 'Условие', exact: true }).click();
    await dialog.getByRole('button', { name: 'OR', exact: true }).click();
    await dialog.getByLabel('Колонка').nth(0).selectOption({ label: 'Номер счёта' });
    await dialog.getByLabel('Значение').nth(0).fill(number);
    await dialog.getByLabel('Колонка').nth(1).selectOption({ label: 'Состояние оплаты' });
    await dialog.getByLabel('Значение').nth(1).selectOption({ label: 'Оплачен' });
    const before = requests.length;
    await dialog.getByRole('button', { name: 'Применить' }).click();

    await chips.getByText('Отбор сложный: условий — 2').waitFor({ timeout: 5000 });
    if ((await chips.getByRole('button', { name: /^Снять условие/ }).count()) !== 0)
      throw new Error('отбор с «ИЛИ» показан чипами — ряд чипов читается как «И»');
    await requestAfter(before);
    if (!lastRequest().includes('"logic":"or"')) throw new Error('в запрос ушло не то дерево, что собрано в диалоге');
    // «Номер равен своему ИЛИ оплачен» — свой счёт находится, хотя он не оплачен. Именно «находится»,
    // а не «ровно он один»: оплаченные счета в общей базе прогонов заводит соседний набор (оплата
    // счёта, C5), и под «ИЛИ» они тоже обязаны быть в выдаче.
    await dataRows().filter({ hasText: number }).first().waitFor({ timeout: 8000 })
      .catch(() => { throw new Error('отбор с «ИЛИ»: своего неоплаченного счёта в выдаче нет'); });
  });
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Чипы отбора');
