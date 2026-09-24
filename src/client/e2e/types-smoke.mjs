// Smoke по редакторам типов и шаблонам (issue #858, порция 8).
//
// Порция вынесла из файлов-компонентов тринадцать функций и хуков: сводку типа поля, сборку
// списка выбираемых типов и его декодирование, реестр редакторов с диалогом-гардом, проверку
// Typst-блоков, превью вариантов перечисления, группировку версий шаблона. Перенос механический,
// и типы его стерегут; НЕ стережёт он одного — что импорт увёл к другому символу с подходящей
// сигнатурой. Такая ошибка не падает: экран показывает не то, оставаясь работоспособным.
//
// Ничего не сохраняет: правки делаются и бросаются вместе с браузером (диалог-гард отвечает
// «Не сохранять»).
//
// Требует поднятых фронта (:5173) и бэка (:5000). Учётка — админская (редакторы типов под
// AdminRoute).
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/types-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

try {

// ── Типы документов: сводка типа поля на свёрнутых карточках ──────────────────
await page.goto(`${BASE}/document-types`);
// Ждём не время, а состояние: на холодном API первый запрос типов доходит позже 3,5 секунды, и
// проверка падала с «тип АОСР не выбрался сам» — то есть красным без поломки. Трижды за сессию.
await page.waitForFunction(() => document.body.innerText.includes('ПАРАМЕТРЫ ТИПА'), null,
  { timeout: 30000 }).catch(() => {});
await page.waitForTimeout(1500);

/**
 * `fieldTypeSummary` разбирает три случая: составной тип (имя из реестра составных), тип поля или
 * перечисление (имя из своего реестра) и базовый скаляр (подпись из TYPE_LABELS). Проверяем все три
 * сразу — увёл бы импорт в другую функцию, пропала бы вся колонка типов или в ней осталась бы
 * техническая строка вроде «complex».
 */
await check('field-summary-covers-three-kinds', async () => {
  const t = await page.locator('body').innerText();
  if (!/АОСР/.test(t)) throw new Error('тип АОСР не выбрался сам — проверять нечего');
  for (const [kind, sample] of [
    ['составной', 'Организация'],
    ['из реестра типов полей', 'Цело число'],
    ['базовый', 'Строка'],
  ]) {
    if (!t.includes(sample)) throw new Error(`нет сводки «${sample}» (${kind})`);
  }
  // Технические имена наружу не выходят: увидели бы их, если бы сводка перестала резолвить типы.
  if (/\bcomplex\b|\bprimitive\b/.test(t)) throw new Error('в сводке техническое имя типа вместо человеческого');
});

// ── Пикер типа поля: список собирает buildFieldTypeOptions ────────────────────
await check('field-type-picker-has-all-sections', async () => {
  await page.locator('button').filter({ hasText: 'Дата начала работ' }).first().click();
  await page.waitForTimeout(1000);
  await page.locator('button').filter({ hasText: /^Дата$/ }).first().click();
  await page.waitForTimeout(1500);
  const t = await page.locator('[role=dialog]').last().innerText();
  for (const section of ['БАЗОВЫЕ', 'ТИПЫ ПОЛЕЙ (РЕЕСТР)', 'ПЕРЕЧИСЛЕНИЯ', 'СОСТАВНЫЕ ТИПЫ']) {
    if (!t.includes(section)) throw new Error(`в пикере нет раздела «${section}»`);
  }
});

/**
 * Выбор пишет обратно пару {type, typeId} — это `decodeFieldType`, разбор строки «вид::цель».
 * Ошибись он видом, поле молча стало бы другим типом, а сводка показала бы что-то третье.
 */
await check('field-type-pick-updates-summary', async () => {
  const picker = page.locator('[role=dialog]').last();
  await picker.locator('button').filter({ hasText: /^Флаг/ }).first().click();
  await page.waitForTimeout(1200);
  const card = page.locator('button').filter({ hasText: 'ДатаНачалаРабот' }).first();
  const t = await card.innerText();
  if (!/Флаг/.test(t)) throw new Error(`сводка поля не стала «Флаг»: ${t.replace(/\s+/g, ' ').slice(0, 120)}`);
});

// ── Реестр редакторов: правка формы поднимается в шапку страницы ──────────────
await check('dirty-badge-reaches-page-header', async () => {
  const t = await page.locator('body').innerText();
  if (!t.includes('есть изменения'))
    throw new Error('шапка не узнала о правке — реестр редакторов до неё не донёс');
});

/**
 * Уход с несохранённым обязан спросить. Это связка реестра (`anyDirty`) и `useDirtyGuard`, которые
 * теперь живут в разных модулях: не сойдись они, переход прошёл бы молча и правка исчезла бы.
 */
await check('leave-guard-asks-before-switching-type', async () => {
  await page.locator('button').filter({ hasText: /^Приложение АОСР/ }).first().click();
  await page.waitForTimeout(1200);
  const t = await page.locator('body').innerText();
  if (!/Несохранённые изменения/.test(t)) throw new Error('переход прошёл без вопроса о несохранённом');
  await page.locator('button').filter({ hasText: /^Не сохранять$/ }).first().click();
  await page.waitForTimeout(2000);
});

// ── Проверка сборки Typst-блоков ──────────────────────────────────────────────
await check('typst-blocks-check-reports-result', async () => {
  await page.locator('button').filter({ hasText: 'Typst-блоки' }).first().click();
  await page.waitForTimeout(1200);
  await page.locator('button').filter({ hasText: 'Проверить блоки' }).first().click();
  // Ждём состояние, а не время: на холодном API первый ответ приходит позже фиксированных
  // четырёх секунд, и проверка краснела БЕЗ поломки — ровно тот же промах, что уже описан у
  // первой проверки этого файла — поэтому срок здесь щедрый, а не подогнанный.
  await page.waitForFunction(
    () => /Все Typst-блоки собираются|Проблемы сборки блоков/.test(document.body.innerText),
    null, { timeout: 25000 },
  ).catch(e => {
    // Своё сообщение — ТОЛЬКО на истечение срока. «Target closed», уход страницы или исключение
    // внутри функции — другие причины, и подмена их на «не вернула результата» увела бы разбор
    // отказа CI по ложному следу: сообщение называло бы не то, что случилось.
    if (e?.name === 'TimeoutError') throw new Error('проверка блоков не вернула результата');
    throw e;
  });
});

// ── Типы полей: превью вариантов перечисления в строке списка ─────────────────
await check('enum-preview-in-list', async () => {
  await page.goto(`${BASE}/field-types`);
  await page.waitForTimeout(3000);
  await page.locator('button').filter({ hasText: /^Перечисления$/ }).first().click();
  await page.waitForTimeout(1500);
  // Утверждение считает: у перечисления с N > 3 вариантами превью показывает три подписи и хвост
  // «(+N−3)». Слабое «где-то на странице есть запятая» тут не годится — оно оставалось бы зелёным
  // и при пустом превью (проверено: запятых на странице хватает и без него).
  const rows = (await page.locator('button').allInnerTexts()).map(s => s.replace(/\s+/g, ' ').trim());
  const long = rows.map(s => [s, /(\d+) вар\./.exec(s)])
                   .filter(([, m]) => m && Number(m[1]) > 3);
  if (!long.length) throw new Error('в реестре нет перечисления длиннее трёх вариантов — проверять нечего');
  for (const [row, m] of long) {
    const tail = `(+${Number(m[1]) - 3})`;
    if (!row.includes(tail)) throw new Error(`у строки «${row.slice(0, 60)}» нет хвоста превью ${tail}`);
  }
});

// ── Шаблоны: версии собраны в группы по имени ─────────────────────────────────
await check('template-versions-grouped-by-name', async () => {
  await page.goto(`${BASE}/templates`);
  await page.waitForTimeout(3000);
  await page.locator('button').filter({ hasText: /Тип документа|Выберите тип/ }).first().click();
  await page.waitForTimeout(1200);
  const picker = page.locator('[role=dialog]').last();
  await picker.locator('button').filter({ hasText: /АОСР/ }).first().click();
  await page.waitForTimeout(3000);
  if (/Выберите тип документа/.test(await page.locator('body').innerText()))
    throw new Error('тип не выбрался');
  // Ищем по кнопкам списка: текст страницы целиком включает и редактор, где «v1» встречается в Typst.
  const labels = (await page.locator('button').allInnerTexts()).map(s => s.replace(/\s+/g, ' ').trim());
  const groups = labels.map(s => /^(.+?) v(\d+)$/.exec(s)).filter(Boolean);
  const versions = labels.map(s => /^v(\d+)\b/.exec(s)).filter(Boolean).map(m => Number(m[1]));
  if (!groups.length || versions.length < 2)
    throw new Error(`нет группы с несколькими версиями — проверять нечего: ${labels.slice(-8).join(' | ')}`);
  // 1. Группировка складывает версии под ОДНО имя: сломайся она — имя стало бы двумя строками.
  const names = groups.map(m => m[1]);
  const dup = names.find((n, i) => names.indexOf(n) !== i);
  if (dup) throw new Error(`имя «${dup}» в списке дважды — версии не сложились в группу`);
  // 2. Внутри группы версии отсортированы по убыванию, поэтому на строке стоит САМАЯ СВЕЖАЯ.
  const shown = Number(groups[0][2]);
  const newest = Math.max(...versions);
  if (shown !== newest)
    throw new Error(`на строке группы v${shown}, а самая свежая версия v${newest} — порядок внутри группы потерян`);
});

/**
 * Счётчик у «Параметров шаблона» — это длина разобранного JSON-объявления (`parseTemplateParams`).
 * Разбор защищён try/catch и на любой беде отдаёт пустой список: сломайся он — панель просто
 * называлась бы «Параметры шаблона» без числа, и объявленные параметры пропали бы молча.
 */
await check('template-params-parsed-from-declaration', async () => {
  const labels = (await page.locator('button').allInnerTexts()).map(s => s.replace(/\s+/g, ' ').trim());
  const panel = labels.find(s => s.startsWith('Параметры шаблона'));
  if (!panel) throw new Error('панели параметров шаблона на странице нет');
  if (!/\(\d+\)$/.test(panel))
    throw new Error(`у панели нет числа параметров — объявление не разобралось: «${panel}»`);
});

// ── Владелец типа (issue #955) ────────────────────────────────────────────────
//
// Проверяется не «поле есть на странице», а то, ради чего признак заведён: владелец виден, его
// можно сменить, и правило «опора ядра — ядро» доезжает до экрана ОТКАЗОМ, называющим тип.
//
// ⚠️ Чего здесь нет и быть не может: «типы выключенного модуля не предлагаются». Модуль в сборке
// один, а единственный модуль не выключить — пустая настройка означает умолчание, а не «ничего».
// Эта ветка проверена чистым тестом (typeOwners.test.ts) и станет проверяемой живьём со вторым
// модулем.
/**
 * Запрос к API ИЗ СТРАНИЦЫ, её же токеном. Из Node это делать нельзя: адрес API у дев-сервера и у
 * собранного клиента в CI разный, а страница в обоих случаях ходит по одному и тому же `/api`.
 */
async function api(method, path, body) {
  return page.evaluate(async ([m, p, b]) => {
    const token = localStorage.getItem('access_token') || sessionStorage.getItem('access_token');
    const res = await fetch(`/api${p}`, {
      method: m,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: b === null ? undefined : JSON.stringify(b),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${m} ${p} → ${res.status} ${text.slice(0, 200)}`);
    return text ? JSON.parse(text) : null;
  }, [method, path, body ?? null]);
}

// Два своих типа вместо чужих: типы заказчика связаны между собой, и «первый попавшийся
// составной» тянет за собой чужие опоры — проверка проверяла бы их, а не правило.
const stamp = Date.now().toString(36).slice(-6);
const OWNED = `Владелец ${stamp}`;
const LEANING = `Опора ${stamp}`;
let ownedId = null;
let leaningId = null;

await page.goto(`${BASE}/composite-types`);
await page.waitForTimeout(3000);

/**
 * Открыть составной тип по имени.
 *
 * ⚠️ Через ПОИСК, а не прямым щелчком по списку: список сгруппирован в аккордеоны, и группа «без
 * группы» по умолчанию свёрнута — её содержимого в DOM нет вовсе. Первая редакция проверки
 * щёлкала по имени напрямую и падала по таймауту, хотя тип был на месте.
 */
async function openComposite(name) {
  const search = page.getByPlaceholder('Поиск типа…');
  await search.fill(name);
  await page.waitForTimeout(1200);
  await page.locator('button').filter({ hasText: name }).first().click();
  await page.waitForTimeout(1500);
}

await check('type-owner-is-visible-and-changeable', async () => {
  ownedId = (await api('POST', '/document-types', {
    name: OWNED, code: `OWN${stamp}`, kind: 'Composite',
    schema: '{"fields":[]}', module: 'core',
  })).id;
  leaningId = (await api('POST', '/document-types', {
    name: LEANING, code: `LEAN${stamp}`, kind: 'Composite',
    schema: '{"fields":[]}', module: 'core',
  })).id;
  await page.reload();
  await page.waitForTimeout(3000);

  await openComposite(OWNED);
  // Утверждение о ЧАСТИ экрана ищется в этой части: селектор владельца адресуется своей ролью и
  // именем, а не текстом всей страницы, где слово «Ядро» могло бы встретиться где угодно.
  const owner = page.getByRole('combobox', { name: 'Владелец' }).first();
  if (!(await owner.count())) throw new Error('в параметрах типа нет выбора владельца');
  const before = (await owner.innerText()).trim();
  if (!['Ядро', 'Исполнительная документация'].includes(before))
    throw new Error(`владелец показан не словами: «${before}»`);

  await owner.click();
  await page.waitForTimeout(600);
  await page.getByRole('option', { name: 'Исполнительная документация' }).first().click();
  await page.waitForTimeout(1500);

  await page.reload();
  await page.waitForTimeout(3000);
  await openComposite(OWNED);
  const after = (await page.getByRole('combobox', { name: 'Владелец' }).first().innerText()).trim();
  if (after !== 'Исполнительная документация')
    throw new Error(`смена владельца не пережила перезагрузку: «${after}»`);
});

await check('core-type-refuses-to-lean-on-a-module-type', async () => {
  // Тип ядра; ставим ему родителем тип, только что отданный модулю.
  await openComposite(LEANING);
  // Пикер адресуется ИМЕНЕМ (подпись связана через aria-labelledby), а не текстом кнопки: текст
  // у неё — выбранное значение, «— без родителя —», и совпал бы с любым другим пустым пикером.
  await page.getByRole('button', { name: /Родительский тип/ }).first().click();
  await page.waitForTimeout(1200);
  await page.locator('[role=dialog]').last().locator('button')
    .filter({ hasText: OWNED }).first().click();
  await page.waitForTimeout(1000);
  await page.locator('button').filter({ hasText: /^Сохранить/ }).first().click();
  await page.waitForTimeout(2500);

  const t = await page.locator('body').innerText();
  if (!/Опора типа ядра обязана принадлежать ядру/.test(t))
    throw new Error('отказ не доехал до экрана');
  // Отказ обязан НАЗВАТЬ тип: без имени чинить нечего, и такой отказ ничем не лучше кода 409.
  if (!t.includes(OWNED))
    throw new Error('в отказе не назван тип, из-за которого он случился');
  // И правка не сохранилась: «почти сохранил» было бы хуже отказа.
  const saved = await api('GET', `/document-types/${leaningId}`);
  if (saved.parentId) throw new Error('родитель всё-таки записался — отказ был только на экране');
});

// Свои типы за собой убираем: следом идут другие прогоны, и база должна остаться той же, какой была.
await check('temporary-types-are-cleaned-up', async () => {
  await api('DELETE', `/document-types/${leaningId}`);
  await api('DELETE', `/document-types/${ownedId}`);
  const left = (await api('GET', '/document-types')).filter(t => t.code.endsWith(stamp));
  if (left.length) throw new Error(`после уборки остались типы: ${left.map(t => t.code).join(', ')}`);
});

} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout. Остальные семь прогонов
// так и делают, этот отставал — и отставание стало заметно, когда набор поехал в CI, где итог
// уходит в лог, а не на терминал: терялись бы ровно те строки, ради которых лог и читают.
process.exitCode = summarize('Smoke редакторов типов и шаблонов');
