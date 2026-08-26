// Smoke по диалогам наборов данных и документов (issue #858, порция 2).
//
// Здесь тоже убраны эффекты-синхронизаторы: «открылось — перелей пропсы в состояние», «закрылось —
// сбрось», «пришёл ответ сервера — скопируй в редактируемое». Замены — монтирование по открытию,
// инициализаторы состояния и локальный ОВЕРРАЙД поверх серверного значения. Ошибка в такой замене
// не падает, а тихо показывает не то: пустой список групп, чужой поиск, забытый выбор шаблонов.
//
// Ничего не сохраняет: правки делаются и бросаются вместе с браузером.
//
// Требует поднятых фронта (:5173), бэка (:5000) и MinIO (:9000) — экран разбиения читает страницы
// PDF из хранилища. Плюс демо-данные, см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/dialogs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';
const PDF_FILE = process.env.SMOKE_PDF_FILE_ID || '688d45ed-834e-4d54-b74f-db1d4220e994';

const AOSR = '250701.ЭОМ-1.АОСР';
const DATASET_FILE = 'Счет на оплату';   // PDF-набор с двумя источниками
const UNION_TYPE = 'Документ произвольный';   // union с вариантами «Документ» и «Проект»

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

const checkedVariant = async () => {
  const dlg = page.locator('[role=dialog]').last();
  const radios = await dlg.getByRole('radio').evaluateAll(els =>
    els.map(e => ({ label: e.innerText.trim(), on: e.getAttribute('aria-checked') === 'true' })));
  return radios.find(r => r.on)?.label ?? null;
};

try {

// ── Разбиение PDF: серверные группы плюс правка поверх ─────────────────────────
await page.goto(`${BASE}/datasets/files/${PDF_FILE}/grouping`);
await page.waitForSelector('img', { timeout: 30000 });
await page.waitForTimeout(1500);
const save = page.getByRole('button', { name: /^Сохранить$/ }).first();

await check('pdf-grouping-loads-groups', async () => {
  const t = await page.locator('body').innerText();
  for (const g of ['Обложка', 'Титульный лист', 'Без группы']) {
    if (!t.includes(g)) throw new Error(`группы «${g}» на экране нет`);
  }
  if ((await page.locator('img').count()) < 1) throw new Error('ни одного листа');
  // Правки ещё не было — сохранять нечего. Если бы «грязно» и содержимое разошлись, здесь бы и
  // вылезло: кнопка активна на нетронутом экране.
  if (!(await save.isDisabled())) throw new Error('«Сохранить» активна на нетронутом разбиении');
});

await check('pdf-grouping-edit-enables-save', async () => {
  await page.locator('img').first().click();
  await page.waitForTimeout(600);
  await page.getByRole('button', { name: /Отделить в новый документ/ }).first().click();
  await page.waitForTimeout(900);
  if (await save.isDisabled()) throw new Error('после правки «Сохранить» так и не стала активной');
  const t = await page.locator('body').innerText();
  if (!/1 документ/.test(t)) throw new Error(`новый документ в сводке не появился: ${t.slice(-300)}`);
});

await check('pdf-page-viewer-opens', async () => {
  await page.getByRole('button', { name: /Просмотреть лист крупно/ }).first().click();
  await page.waitForTimeout(2500);
  const dlg = page.locator('[role=dialog]').last();
  if (!/Лист \d/.test(await dlg.innerText())) throw new Error('заголовка «Лист N» нет');
  const img = dlg.locator('img').first();
  await img.waitFor({ state: 'attached', timeout: 20000 })
    .catch(() => { throw new Error('изображение листа не появилось за 20 с'); });
  const ok = await img.evaluate(el => el.complete && el.naturalWidth > 0);
  if (!ok) throw new Error('изображение листа не разобралось (naturalWidth = 0)');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(600);
});

// ── Материализация: активный вариант union ────────────────────────────────────
await page.goto(`${BASE}/datasets`);
await page.waitForTimeout(2500);
await page.getByText(DATASET_FILE, { exact: false }).first().click();
await page.waitForTimeout(2500);
await page.locator('button[aria-haspopup]').nth(1).click();   // кебаб первого источника
await page.waitForTimeout(700);
await page.getByText('Материализация', { exact: false }).first().click();
await page.waitForTimeout(1800);

await check('materialize-dialog-opens', async () => {
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/Материализация источника/.test(t)) throw new Error(`не тот диалог: ${t.slice(0, 140)}`);
});

await check('materialize-union-shows-variants', async () => {
  const dlg = page.locator('[role=dialog]').last();
  await dlg.getByText('не материализовать', { exact: false }).first().click();
  await page.waitForTimeout(1000);
  const picker = page.locator('[role=dialog]').last();
  await picker.locator('input').first().fill(UNION_TYPE);
  await page.waitForTimeout(800);
  await picker.getByText(UNION_TYPE, { exact: true }).first().click();
  await page.waitForTimeout(1800);
  const on = await checkedVariant();
  if (on !== 'Документ') throw new Error(`активен вариант «${on}», ждали первый — «Документ»`);
});

// Ключевая проверка порции. Раньше активный вариант был копией, которую эффект переливал из
// маппинга; переключение на НЕзамапленный вариант оставляло маппинг пустым, эффект видел «нет
// замапленного» и тут же возвращал первый вариант — выбрать второй было нельзя вовсе.
await check('materialize-union-switch-to-empty-variant-sticks', async () => {
  const dlg = page.locator('[role=dialog]').last();
  const select = dlg.locator('select').first();
  if (!(await select.count())) throw new Error('нет селектора колонки — нечего маппить');
  const columns = await select.locator('option').evaluateAll(o => o.map(x => x.textContent.trim()));
  const column = columns.find(c => c && !/не привязано/i.test(c));
  if (!column) throw new Error('в селекторе нет ни одной колонки источника');
  await select.selectOption({ label: column });
  await page.waitForTimeout(900);
  if ((await checkedVariant()) !== 'Документ') throw new Error('маппинг сбил активный вариант');

  await dlg.getByRole('radio', { name: /^Проект$/ }).first().click();
  await page.waitForTimeout(1000);
  const on = await checkedVariant();
  if (on !== 'Проект') throw new Error(`после переключения активен «${on}» — выбор не удержался`);
});

await page.keyboard.press('Escape');
await page.waitForTimeout(800);

// ── Копирование документа: цель забывается при закрытии ────────────────────────
await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
await page.waitForSelector('tbody tr', { timeout: 15000 });
await page.waitForTimeout(1000);

const openCopy = async () => {
  await page.locator('tbody tr').first().locator('button[aria-haspopup]').first().click();
  await page.waitForTimeout(700);
  await page.getByText('Скопировать', { exact: false }).first().click();
  await page.waitForTimeout(1200);
};

await check('copy-dialog-opens-on-set-picker', async () => {
  await openCopy();
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать в комплект/.test(t)) throw new Error(`не пикер комплекта: ${t.slice(0, 160)}`);
});

await check('copy-dialog-reopens-at-picker-not-confirm', async () => {
  const picker = page.locator('[role=dialog]').last();
  const target = picker.getByRole('option').or(picker.locator('button')).filter({ hasText: /ЭОМ|СКС|ОВиК/ }).first();
  if (!(await target.count())) throw new Error('в пикере нет ни одного комплекта-цели');
  await target.click();
  await page.waitForTimeout(1500);
  const confirm = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать «/.test(confirm)) throw new Error(`подтверждение не открылось: ${confirm.slice(0, 200)}`);
  await page.keyboard.press('Escape');   // отмена — цель обязана забыться вместе с закрытием
  await page.waitForTimeout(900);
  await openCopy();
  const again = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать в комплект/.test(again))
    throw new Error(`повторное открытие показало не пикер, а: ${again.slice(0, 200)}`);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(700);
});

// ── Вкладка генерации: выбор шаблонов ─────────────────────────────────────────
await page.getByText(AOSR, { exact: false }).first().click();
await page.waitForSelector('[role=dialog]', { timeout: 15000 });
await page.waitForTimeout(2000);
const editor = page.locator('[role=dialog]').first();

await check('generation-tab-shows-templates', async () => {
  // Вкладки редактора — не role=button (getByRole их не видит), поэтому ищем по тексту кнопки.
  await editor.locator('button').filter({ hasText: /^Генерация$/ }).first().click();
  await page.waitForTimeout(2000);
  const t = await editor.innerText();
  if (!/Статус:/.test(t)) throw new Error(`вкладка генерации не открылась: ${t.slice(0, 200)}`);
  const boxes = await editor.locator('input[type=checkbox]').count();
  if (boxes < 1) throw new Error('ни одного шаблона в списке — проверять выбор не на чем');
});

await check('generation-tab-selection-follows-click', async () => {
  const box = editor.locator('input[type=checkbox]').first();
  const before = await box.isChecked();
  await box.click();
  await page.waitForTimeout(1200);
  if ((await box.isChecked()) === before) throw new Error('галка не переключилась');
  await box.click();   // возвращаем как было
  await page.waitForTimeout(1200);
  if ((await box.isChecked()) !== before) throw new Error('галка не вернулась в исходное состояние');
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke диалогов наборов и документов');
