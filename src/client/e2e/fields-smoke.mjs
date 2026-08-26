// Smoke по диалогам полей документа (issue #858).
//
// Эти экраны юнит-тестами не покрыты, а править их пришлось по-настоящему: эффекты, которые
// синхронизировали состояние с пропсами («открылось — перелей items в rows», «закрылось — сбрось
// второй шаг», «значение пришло — подхвати вариант»), заменены на пересоздание поддерева и
// производные значения. Ломается такое молча: снимок строк приезжает пустым, диалог открывается
// с чужим состоянием, активный вариант union'а не тот. Прогон смотрит на это глазами браузера:
//   • таблица массива показывает ВСЕ строки уже на первом рендере;
//   • вставка из Excel разбирает текст и уводит на шаг сопоставления, посчитав строки;
//   • пикер ссылки находит кандидатов, сужается поиском и открывается ЧИСТЫМ после закрытия;
//   • union-строка открывается на заполненном варианте, а переключение помнит спрятанное;
//   • предпросмотр вложения и поле-картинка показывают файл из хранилища (blob:-URL живой).
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных — см. e2e/README.md. Адреса
// комплекта и стройки переопределяются через SMOKE_SET_ID / SMOKE_CONSTRUCTION_ID.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/fields-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';

// Документы демо-комплекта, на которых стоят проверки, и то, ЧЕМ они для прогона ценны.
const WORKS = '250701.ЭОМ-1.1.Реестр работ';   // массив «Работы»: 19 ВСТРОЕННЫХ строк (не ссылок)
const AOSR = '250701.ЭОМ-1.АОСР';              // ссылки в «Членах комиссии» + union в «Документах соответствия»
const PDF_DOC = 'Кабельный журнал (из PDF)';   // поле-файл с настоящим вложением
const WORKS_ROWS = 19;

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

async function openInstance(name) {
  await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  await page.getByText(name, { exact: false }).first().click();
  await page.waitForSelector('[role=dialog]', { timeout: 15000 });
  await page.waitForTimeout(2000);
  return page.locator('[role=dialog]').first();
}

try {

// ── Таблица массива ────────────────────────────────────────────────────────────
// «Реестр работ» выбран за данные: строки массива ВСТРОЕННЫЕ, значит таблица обязана показать их
// все. У АОСР члены комиссии хранятся ссылками, и там пустая таблица — законный ответ.
let editor = await openInstance(WORKS);
if ((await editor.getByRole('button', { name: /^Таблица$/ }).count()) === 0) {
  await editor.getByRole('button', { name: /Работы/ }).first().click();
  await page.waitForTimeout(900);
}
await editor.getByRole('button', { name: /^Таблица$/ }).first().click();
await page.waitForTimeout(1200);
const table = page.locator('[role=dialog]').last();

await check('array-table-opens', async () => {
  const t = await table.innerText();
  if (!/таблица/i.test(t)) throw new Error(`открылся не тот диалог: ${t.slice(0, 140)}`);
  if ((await table.locator('th').count()) < 1) throw new Error('нет заголовков колонок');
});

await check('array-table-rows-on-first-render', async () => {
  const rows = await table.locator('tbody tr').count();
  if (rows !== WORKS_ROWS) throw new Error(`ожидали ${WORKS_ROWS} строк, нашли ${rows}`);
});

// ── Вставка из Excel ───────────────────────────────────────────────────────────
await table.getByRole('button', { name: /вставить из excel/i }).first().click();
await page.waitForTimeout(900);

await check('paste-opens-on-input-step', async () => {
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/вставьте|excel/i.test(t)) throw new Error(`не шаг ввода: ${t.slice(0, 140)}`);
});

await check('paste-detects-header-and-counts-rows', async () => {
  const dlg = page.locator('[role=dialog]').last();
  await dlg.locator('textarea').first().fill('Наименование\tЕдиница\nКабель ВВГнг 3х2.5\tм');
  await dlg.getByRole('button', { name: /далее/i }).first().click();
  await page.waitForTimeout(900);
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/сопоставлен/i.test(t)) throw new Error(`шаг сопоставления не открылся: ${t.slice(0, 200)}`);
  // Строка заголовков распознана и в данные НЕ попала — иначе счётчик показал бы 2.
  if (!/импортировать\s*\(1 стр/i.test(t)) throw new Error(`строки посчитаны неверно: ${t.slice(-300)}`);
});

await page.keyboard.press('Escape');   // закрыть вставку
await page.waitForTimeout(600);
await page.keyboard.press('Escape');   // закрыть таблицу
await page.waitForTimeout(800);

// ── Пикер ссылки ───────────────────────────────────────────────────────────────
editor = await openInstance(AOSR);
await editor.getByRole('button', { name: /Члены комиссии/ }).first().click();
await page.waitForTimeout(900);

// Считаем кандидатов один раз и сверяемся с этим числом дальше: на пустом списке проверка
// «поиск сузил» истинна при любом коде, поэтому непустота — отдельное утверждение.
let candidates = 0;
await check('ref-picker-shows-candidates', async () => {
  await editor.getByRole('button', { name: /^Из каталога$/ }).first().click();
  await page.waitForTimeout(1500);
  const dlg = page.locator('[role=dialog]').last();
  const t = await dlg.innerText();
  if (!/выбрать объект/i.test(t)) throw new Error(`не тот диалог: ${t.slice(0, 160)}`);
  candidates = await dlg.locator('[role=option]').count();
  if (candidates < 1) throw new Error('в пикере нет ни одного кандидата');
});

await check('ref-picker-search-narrows-and-restores', async () => {
  if (candidates < 1) throw new Error('нечего сужать — кандидатов не было');
  const dlg = page.locator('[role=dialog]').last();
  const input = dlg.locator('input').first();
  await input.fill('щщщ-такого-нет');
  await page.waitForTimeout(700);
  const after = await dlg.locator('[role=option]').count();
  if (after !== 0) throw new Error(`список не сузился: было ${candidates}, стало ${after}`);
  await input.fill('');
  await page.waitForTimeout(700);
  const back = await dlg.locator('[role=option]').count();
  if (back !== candidates) throw new Error(`список не вернулся: было ${candidates}, стало ${back}`);
});

// До #858 пикер был смонтирован постоянно, и набранный поиск переживал закрытие: следующее
// открытие показывало отфильтрованный список без единого намёка на причину.
await check('ref-picker-reopens-clean', async () => {
  const dlg0 = page.locator('[role=dialog]').last();
  await dlg0.locator('input').first().fill('надзор');
  await page.waitForTimeout(500);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(800);
  await editor.getByRole('button', { name: /^Из каталога$/ }).first().click();
  await page.waitForTimeout(1300);
  const dlg = page.locator('[role=dialog]').last();
  if (!/выбрать объект/i.test(await dlg.innerText())) throw new Error('открылось не «Выбрать объект»');
  const val = await dlg.locator('input').first().inputValue();
  if (val !== '') throw new Error(`поиск не сброшен: «${val}»`);
  const opts = await dlg.locator('[role=option]').count();
  if (opts !== candidates) throw new Error(`кандидатов ${opts}, при первом открытии было ${candidates}`);
});

// ── Union-поле ─────────────────────────────────────────────────────────────────
// «Документы соответствия» — массив union-типа: строка 1 заполнена вариантом «Проект»,
// строка 2 — вариантом «Документ». По строке видно, какой вариант обязан быть активным.
await page.keyboard.press('Escape');
await page.waitForTimeout(700);
await editor.getByRole('button', { name: /Документы соответствия/ }).first().click();
await page.waitForTimeout(1000);
await editor.getByRole('button', { name: /Документы соответствия/ }).nth(1).click();
await page.waitForTimeout(1000);

// Переключатель вариантов — radio-группа (VariantPicker), активный помечен aria-checked.
const checkedVariant = async () => {
  const dlg = page.locator('[role=dialog]').last();
  const radios = await dlg.getByRole('radio').evaluateAll(els =>
    els.map(e => ({ label: e.innerText.replace(/задан/g, '').trim(), on: e.getAttribute('aria-checked') === 'true' })));
  return radios.find(r => r.on)?.label ?? null;
};

await check('union-row-opens-on-filled-variant', async () => {
  await editor.getByRole('button', { name: /^Редактировать$/ }).first().click();
  await page.waitForTimeout(1600);
  const on = await checkedVariant();
  if (on !== 'Проект') throw new Error(`активен вариант «${on}», ждали «Проект»`);
  if (!/Проект ЭОМ/.test(await page.locator('[role=dialog]').last().innerText()))
    throw new Error('содержимое активного варианта не показано');
});

await check('union-switch-hides-and-restores', async () => {
  const dlg = page.locator('[role=dialog]').last();
  await dlg.getByRole('radio', { name: /^Документ$/ }).first().click();
  await page.waitForTimeout(900);
  if ((await checkedVariant()) !== 'Документ') throw new Error('переключение не сменило активный вариант');
  if (/Проект ЭОМ/.test(await dlg.innerText())) throw new Error('показан прежний вариант при другом активном');
  await dlg.getByRole('radio', { name: /^Проект$/ }).first().click();
  await page.waitForTimeout(900);
  if ((await checkedVariant()) !== 'Проект') throw new Error('возврат не сменил активный вариант');
  if (!/Проект ЭОМ/.test(await dlg.innerText())) throw new Error('спрятанное значение не вернулось из стэша');
});

await check('union-second-row-has-its-own-variant', async () => {
  await page.keyboard.press('Escape');
  await page.waitForTimeout(900);
  await editor.getByRole('button', { name: /^Редактировать$/ }).nth(1).click();
  await page.waitForTimeout(1600);
  const on = await checkedVariant();
  if (on !== 'Документ') throw new Error(`активен вариант «${on}», ждали «Документ»`);
  if (!/ПУЭ 7/.test(await page.locator('[role=dialog]').last().innerText()))
    throw new Error('содержимое активного варианта не показано');
});

// ── Вложение и картинка ────────────────────────────────────────────────────────
// Оба поля тянут байты из хранилища и показывают их через объект-URL. Проверяем не «есть тег
// img», а что браузер эту картинку РАЗОБРАЛ: отозванный или чужой URL даёт naturalWidth = 0.
editor = await openInstance(PDF_DOC);

await check('file-preview-shows-attachment', async () => {
  const eye = editor.getByRole('button', { name: /предпросмотр/i })
    .or(editor.locator('button[title="Предпросмотр"]')).first();
  if (!(await eye.count())) throw new Error('кнопки предпросмотра у поля-файла нет');
  await eye.click();
  await page.waitForTimeout(2500);
  const dlg = page.locator('[role=dialog]').last();
  if ((await dlg.locator('iframe').count()) + (await dlg.locator('img').count()) < 1)
    throw new Error(`ни iframe, ни img: ${(await dlg.innerText()).slice(0, 200)}`);
  const src = await dlg.locator('iframe, img').first().getAttribute('src');
  if (!src || !src.startsWith('blob:')) throw new Error(`ожидали blob:-URL, получили «${src}»`);
});

await page.keyboard.press('Escape');
await page.waitForTimeout(600);
await page.keyboard.press('Escape');
await page.waitForTimeout(800);

await check('image-field-renders-stored-image', async () => {
  await page.goto(`${BASE}/common-data`);
  await page.waitForTimeout(2500);
  // Записи показываются по типу, а группа полей свёрнута: без обоих кликов поле не смонтировано.
  await page.getByText('Организация в СРО', { exact: false }).first().click();
  await page.waitForTimeout(1500);
  await page.getByText('Техногид', { exact: false }).first().click();
  await page.waitForTimeout(2500);
  await page.getByText('ЛОГОТИП, ПЕЧАТЬ', { exact: false }).first().click();
  await page.waitForTimeout(3500);
  const imgs = page.locator('img[src^="blob:"]');
  if ((await imgs.count()) < 1) throw new Error('ни одной картинки с blob:-URL');
  const ok = await imgs.first().evaluate(el => el.complete && el.naturalWidth > 0);
  if (!ok) throw new Error('картинка не разобралась (naturalWidth = 0) — URL отозван или не тот');
});

} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout при выводе в файл.
process.exitCode = summarize('Smoke диалогов полей');
