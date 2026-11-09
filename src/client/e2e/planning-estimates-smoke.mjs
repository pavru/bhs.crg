// Smoke экранов смет модуля «Планирование» (change `plan-09-estimate-ui`, задача 4.4, ТЗ PLAN-20, PLAN-5.2,
// PLAN-6.1, PLAN-7, PLAN-9.1).
//
// ЗАЧЕМ ОН ЕСТЬ. Путь сметчика — «файл → редакция → сопоставление» — собран из окон, и каждое по отдельности
// проверено vitest на подделанных ответах. Здесь он идёт по живому серверу: разбор фоновой задачей, версии
// редакции между окнами, права трёх ролей.
//
//   1. `estimator-imports-a-revision` — «Сметчик» загружает файл новой редакцией из карточки сметы: отчёт
//      «Разобрано N из N», «Создать редакцию» — и карточка на ней, с отбором «Не сопоставлены».
//   2. `estimator-matches-a-section` — «Сопоставить раздел»: вид работы, коэффициент сервера с примером,
//      сопоставлены строки одной единицы, вид работы стоит в строках.
//   3. `estimator-matches-a-material` — «Сопоставить материалы»: поиск по номенклатуре находит позицию,
//      выбор ложится на материал, окно ведёт к следующему.
//   3а. `estimator-distributes-a-section` — режим «Распределение» (plan-10, ТЗ PLAN-5.1): правило раздела ЛСР
//      списком в заголовке — запись сразу, полоса «распределено N из M», «Отменить» в снэкбаре возвращает
//      правило; окно строки: часть в раздел, остаток — «стройка в целом».
//   4. `reader-sees-distribution-read-only` и `reader-sees-cost-hidden` — «Менеджер проекта» (только
//      `plan.estimate.read`): распределение видно без правки; колонка стоимости «скрыто правами», действий
//      правки нет.
//   5. `id-engineer-sees-no-estimates` — «Инженер ИД»: ни раздела «Планы» в рейле, ни пункта «Сметы» у стройки.
//
// ⚠️ Данные — посев (`seed-estimates.mjs`): смета «ЭОМ корпус 2 (посев)» с двумя редакциями и
// пользователи. Вид работы прогон заводит СЕБЕ САМ (один раз, по имени): классификатор видов работ
// появляется только со следующего запуска приложения после посева единиц. Редакции копятся от
// запуска к запуску. Чтобы каждая новая ждала сопоставления, прогон НЕ запоминает (снимает флажок) и
// перед началом забывает запомненное по шифрам и материалам своей сметы — иначе второй запуск получал
// бы строки уже сопоставленными, и проверять было бы нечего. Забывает дословно по словам файла посева и
// только запомненное сметчиком посева: шифры посева — настоящие шифры ГЭСН, а запомненное людьми не его.
//
// Требует поднятых фронта и бэка, посева, перезапуска API после него и ВКЛЮЧЁННОГО модуля `plan`.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/planning-estimates-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, clearSession, pickOption, railSection } from './harness.mjs';
import { ESTIMATE_NAME, ESTIMATOR_NAME, estimateXml, seedMatchKeys } from './seed-estimates.mjs';

const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const ESTIMATOR = process.env.SMOKE_ESTIMATOR_EMAIL || 'smeta@bhs.local';
const PM = process.env.SMOKE_PM_EMAIL || 'pm@bhs.local';
const ENGINEER = process.env.SMOKE_USER_EMAIL || 'petrov@bhs.local';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID;
const WORK_TYPE = 'Монтаж электроустановочных изделий (прогон)';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

/** Запрос тем же токеном, что у страницы; отказ — исключением с текстом сервера. */
async function api(method, path, body) {
  const r = await callApi(page, method, path, body);
  if (r.status >= 400) throw new Error(`${method} ${path} → ${r.status} ${String(r.body).slice(0, 300)}`);
  return r.body;
}

/** Вид работы прогона: в штуках — для розеток и выключателей раздела 1 («100 шт» → «шт», × 100). */
async function ensureWorkType() {
  const types = await api('GET', '/work-types?purpose=choice');
  if (types.some(t => t.displayName === WORK_TYPE)) return;
  const unitType = (await api('GET', '/document-types')).find(t => t.code === 'ЕдиницаИзмерения');
  const piece = unitType && (await api('GET', `/common-data?purpose=choice&typeId=${unitType.id}`)).find(u => u.displayName === 'шт');
  if (!piece) throw new Error('в справочнике единиц нет «шт» — посев смет не отработал');
  const made = await callApi(page, 'POST', '/work-types', {
    displayName: WORK_TYPE,
    data: JSON.stringify({ Код: '99.01', Наименование: WORK_TYPE, ЕдиницаИзмерения: { $ref: 'catalog', entryId: piece.id, displayName: 'шт' } }),
  });
  if (made.status === 409)
    throw new Error(`классификатора видов работ нет: он появляется со следующего запуска после посева единиц — перезапустите API (${made.body})`);
  if (made.status >= 400) throw new Error(`вид работы не заведён: ${made.status} ${made.body}`);
}

/**
 * Забыть запомненное по шифрам и материалам сметы посева: следующий импорт обязан ждать сопоставления. Только
 * дословно по словам файла и только запомненное сметчиком посева (`ESTIMATOR` или его имя). Запомненное другим
 * человеком прогон не трогает — отказывает: на рабочей базе это чужие данные, а прогон по ним проверял бы не то.
 */
async function forgetRemembered() {
  const keys = seedMatchKeys();
  for (const [list, field, words] of [['rate-matches', 'rateCode', keys.rate], ['resource-matches', 'sourceText', keys.resource]])
    for (const word of words) {
      const found = await api('GET', `/plan/${list}?query=${encodeURIComponent(word)}&take=200`);
      for (const item of found.items.filter(i => i[field] === word)) {
        if (item.updatedBy !== ESTIMATOR_NAME && item.updatedBy !== ESTIMATOR)
          throw new Error(`«${word}» запомнил ${item.updatedBy ?? 'неизвестно кто'}, а не сметчик посева — прогон чужое не `
            + 'забывает. Забудьте его в «Планы» › «Запомненные сопоставления» или запускайте прогон на стенде с посевом');
        const r = await page.evaluate(async ([url, version]) => {
          const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
          return (await fetch(url, { method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': version } })).status;
        }, [`/api/plan/${list}/${item.id}`, item.version]);
        if (r >= 400) throw new Error(`запомненное «${item[field]}» не забыто: ${r}`);
      }
    }
}

/** Смета посева, как её отдаёт сервер, — с редакциями. */
const seededEstimate = async () =>
  (await api('GET', `/plan/estimates?constructionId=${CONSTRUCTION}`)).items.find(e => e.name === ESTIMATE_NAME) ?? null;

const dialog = () => page.getByRole('dialog');
const card = estimateId => page.goto(`${BASE}/estimates/${estimateId}`, { waitUntil: 'networkidle' });
const linesRead = () => page.getByText('Читаем строки редакции…').waitFor({ state: 'detached', timeout: 15_000 });
const distributionRead = () => page.getByText('Читаем распределение…').waitFor({ state: 'detached', timeout: 15_000 });
const escape = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

let estimate;
try {
  await login(page, ESTIMATOR, PASSWORD);
  await page.goto(`${BASE}/estimates`, { waitUntil: 'networkidle' });
  // Отказы до проверок — исключением, а не process.exit: иначе `finally` не закрыл бы браузер.
  if ((await page.getByRole('heading', { name: 'Сметы' }).count()) === 0)
    throw new Error('Экрана «Сметы» нет. Включён ли модуль plan у сервера (Modules__Enabled=id,costs,plan) и есть ли у '
      + `«${ESTIMATOR}» роль «Сметчик»? Без него проверять нечего.`);
  if (!CONSTRUCTION) throw new Error('SMOKE_CONSTRUCTION_ID не задан — посев не отработал или его переменные не переданы');
  estimate = await seededEstimate();
  if (!estimate) throw new Error(`Сметы «${ESTIMATE_NAME}» нет — посев смет не отработал (модуль plan был выключен при посеве?).`);
  await ensureWorkType();
  await forgetRemembered();

  // ── 1. Новая редакция из файла ─────────────────────────────────────────────────────────────────
  await check('estimator-imports-a-revision', async () => {
    await card(estimate.id);
    // Редакции считаются у сервера, а не вкладками: их копится по одной на запуск, и вкладки сверх места их
    // число уже не показывают.
    const before = (await seededEstimate()).revisions.length;
    await page.getByRole('button', { name: 'Новая редакция' }).click();
    // Площадка файла ([UI-89], sharedui-file-drop-zone): брошенный файл не того вида получает отказ на месте,
    // и разбор не начинается. Бросок — настоящим событием с DataTransfer, путь тот же, что у мыши.
    const pdf = await page.evaluateHandle(() => {
      const data = new DataTransfer();
      data.items.add(new File(['%PDF-1.4'], 'счёт.pdf', { type: 'application/pdf' }));
      return data;
    });
    await dialog().getByText('Перетащите файл сметы сюда').dispatchEvent('drop', { dataTransfer: pdf });
    await dialog().getByRole('alert').filter({ hasText: 'счёт.pdf — не .gsfx или .xml' }).waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('брошенный .pdf не получил отказа «не .gsfx или .xml» под площадкой'); });
    if (await dialog().getByRole('button', { name: 'Загрузить' }).isEnabled())
      throw new Error('после отказа .pdf «Загрузить» доступна — файл всё же взят');
    await dialog().getByLabel('Файл сметы').setInputFiles({
      name: 'ЛСР прогона.xml', mimeType: 'application/xml', buffer: Buffer.from(estimateXml(true), 'utf8'),
    });
    await dialog().getByRole('button', { name: 'Загрузить' }).click();
    await dialog().getByText(/Разобрано 6 из 6 позиций/).waitFor({ timeout: 60_000 });
    await dialog().getByRole('button', { name: 'Создать редакцию' }).click();
    await dialog().waitFor({ state: 'detached', timeout: 15_000 });

    // Карточка на созданной редакции, с отбором «не сопоставлено» — его и разбирают. Ждём НАЖАТЫЙ отбор: окно
    // открыто из карточки той же сметы, и не нажатый отбор прежней редакции стоит на экране до перехода.
    await page.getByRole('button', { name: /^Не сопоставлены/, pressed: true }).waitFor({ timeout: 15_000 })
      .catch(() => { throw new Error('карточка открылась без отбора «Не сопоставлены»'); });
    const after = (await seededEstimate()).revisions.length;
    if (after !== before + 1) throw new Error(`редакций у сметы было ${before}, стало ${after}`);
  });

  // ── 2. Раздел целиком ─────────────────────────────────────────────────────────────────────────
  await check('estimator-matches-a-section', async () => {
    await linesRead();
    await page.getByRole('button', { name: 'Сопоставить раздел' }).first().click();
    await pickOption(dialog().getByRole('combobox', { name: 'Вид работы' }), `99.01 ${WORK_TYPE}`);
    // Коэффициент — от сервера, с примером на строке: «100 шт» в «шт» — сто.
    await dialog().getByText(/например, 1: 2,16 × 100 = 216 шт/).waitFor({ timeout: 10_000 });
    await dialog().getByRole('checkbox', { name: /^Запомнить/ }).uncheck();
    const button = dialog().getByRole('button', { name: /^Сопоставить \d+ строк/ });
    const label = (await button.innerText()).trim();
    if (label !== 'Сопоставить 2 строки') throw new Error(`ждали «Сопоставить 2 строки» (единица «100 шт»), а на кнопке «${label}»`);
    await button.click();
    await dialog().waitFor({ state: 'detached', timeout: 15_000 });
    await settled(page);

    // Отбор «не сопоставлено» снят с сопоставленных — смотрим всю редакцию.
    await page.getByRole('button', { name: /^Все/ }).click();
    // Раздел раскрываем, если он свёрнут: щелчок по раскрытому свернул бы его.
    const heading = page.getByRole('button', { name: /^Раздел 1/ });
    if ((await heading.getAttribute('aria-expanded', { timeout: 5_000 })) === 'false') await heading.click();
    if ((await page.getByRole('button', { name: WORK_TYPE }).count()) < 2)
      throw new Error(`вид работы «${WORK_TYPE}» не встал в сопоставленные строки`);
  });

  // ── 3. Материал — одним решением на все строки ────────────────────────────────────────────────
  await check('estimator-matches-a-material', async () => {
    await page.getByRole('button', { name: 'Сопоставить материалы…' }).click();
    const place = dialog().getByText(/^Материал 1 из (\d+)/);
    await place.waitFor({ timeout: 15_000 });
    const total = Number((await place.innerText()).match(/из (\d+)/)[1]);
    await dialog().getByRole('checkbox', { name: /^Запомнить/ }).uncheck();
    await dialog().getByRole('textbox', { name: 'Поиск позиции' }).fill('посев');
    const option = dialog().getByRole('option').first();
    await option.waitFor({ timeout: 10_000 });
    await option.click();
    // Следующий материал — в том же окне; последний — «пройдены все».
    if (total > 1) await dialog().getByText(new RegExp(`^Материал 2 из ${total}`)).waitFor({ timeout: 15_000 });
    else await dialog().getByText('Пройдены все материалы без номенклатуры.').waitFor({ timeout: 15_000 });
  });

  // ── 3а. Распределение по разделам стройки (plan-10) ───────────────────────────────────────────
  await check('estimator-distributes-a-section', async () => {
    // Окно материалов прошлой проверки ещё открыто на следующем материале — карточку открываем заново.
    await card(estimate.id);
    await page.getByRole('radio', { name: 'Распределение' }).click();
    await distributionRead();
    // Правило раздела ЛСР — списком в заголовке раздела; цель — первый раздел стройки посева.
    const rule = page.getByRole('combobox', { name: /^Правило: Раздел 1/ });
    await rule.click();
    // Первый пункт списка — «Не распределено» (снять правило); цель — первый раздел из группы разделов стройки.
    const target = page.getByRole('group', { name: 'Разделы стройки' }).getByRole('option').first();
    const name = (await target.innerText()).trim();
    await target.click();
    // Запись сразу: снэкбар называет цель и предлагает «Отменить», полоса редакции считает строки раздела.
    await page.getByText(new RegExp(`Раздел 1 → ${escape(name)}`)).waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error(`после выбора «${name}» нет снэкбара «Раздел 1 → ${name}»`); });
    await page.getByText(/распределено [1-9]\d* из/).waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('полоса редакции не показала распределённые строки после правила'); });
    await page.getByRole('button', { name: 'Отменить' }).click();
    await page.getByText('Правило возвращено').first().waitFor({ timeout: 10_000 })
      .catch(e => { throw new Error(`«Отменить» не вернуло правило: ${e.message.split(/\r?\n/)[0]}`); });
    if (!(await rule.innerText()).includes('Не распределено')) throw new Error(`после «Отменить» правило — «${await rule.innerText()}»`);

    // Строку — частью в раздел окном строки: два связанных поля единиц, остаток — «стройка в целом».
    const heading = page.getByRole('button', { name: /^Раздел 1/ });
    if ((await heading.getAttribute('aria-expanded', { timeout: 5_000 })) === 'false') await heading.click();
    await page.getByRole('button', { name: 'Разделить…' }).first().click();
    await pickOption(dialog().getByRole('combobox', { name: 'Раздел стройки, часть 1' }), name);
    await dialog().getByRole('textbox', { name: 'В ед. строки' }).fill('1');
    await dialog().getByText(/^1 из .* · стройка в целом: /).waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('окно строки не назвало остаток «стройка в целом»'); });
    await dialog().getByRole('button', { name: 'Записать' }).click();
    await dialog().waitFor({ state: 'detached', timeout: 15_000 });
    await page.getByText(`${name} + стройка в целом`).first().waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error(`строка не показала «${name} + стройка в целом»`); });
  });

  // ── 4. Без права на себестоимость ─────────────────────────────────────────────────────────────
  await clearSession(page);
  await login(page, PM, PASSWORD);
  await check('reader-sees-distribution-read-only', async () => {
    await card(estimate.id);
    await page.getByRole('radio', { name: 'Распределение' }).click();
    await distributionRead();
    await page.getByText(/^Раздел 1/).first().waitFor({ timeout: 10_000 });
    if (await page.getByRole('combobox', { name: /^Правило:/ }).count()) throw new Error('у читающего смету правило раздела правится');
    if (await page.getByRole('button', { name: 'Разделить…' }).count()) throw new Error('у читающего смету есть «Разделить…»');
    await page.getByRole('radio', { name: 'Сопоставление' }).click();
  });
  await check('reader-sees-cost-hidden', async () => {
    await card(estimate.id);
    await linesRead();
    // Разделы свёрнуты — причина видна в клетках строк раскрытого.
    await page.getByRole('button', { name: /^Раздел / }).first().click();
    await page.getByText('скрыто правами').first().waitFor({ timeout: 10_000 });
    for (const name of ['Новая редакция', 'Сопоставить раздел', 'Сопоставить материалы…', 'Сделать действующей'])
      if (await page.getByRole('button', { name }).count()) throw new Error(`у читающего смету есть действие «${name}»`);
  });

  // ── 5. Инженер ИД смет не видит ──────────────────────────────────────────────────────────────
  await clearSession(page);
  await login(page, ENGINEER, PASSWORD);
  await check('id-engineer-sees-no-estimates', async () => {
    await page.goto(`${BASE}/document-sets/${CONSTRUCTION}`, { waitUntil: 'networkidle' });
    await settled(page);
    if (await railSection(page, 'Планы').count()) throw new Error('у «Инженера ИД» в рейле есть раздел «Планы»');
    if (await page.getByRole('link', { name: /^Сметы/ }).count()) throw new Error('у «Инженера ИД» в карточке стройки есть пункт «Сметы»');
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Сметы');
