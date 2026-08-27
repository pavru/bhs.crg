// Smoke по экранам, где выбор зависел от эффекта (issue #858, порция 5 — последняя по правилу).
//
// Здесь эффекты не переливали форму, а вели ВЫБОР: сбрасывали выбранный шаблон при смене типа,
// раскрывали группу выбранного, открывали документ по адресу `?doc=`, разбирали `?view=` на
// странице сверки, подставляли первого кандидата в диалоге источника. Всё это стало вычислением,
// и ломается оно тихо: не то открыто, не то подставлено, выбор от прошлого типа.
//
// Ничего не сохраняет.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/pages-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';
const AOSR_INSTANCE = process.env.SMOKE_INSTANCE_ID || 'b1de57a0-6c14-4bbc-9cad-1dda592c9c66';
const SYSTEM_DATASET = 'Данные системы';
/** Тип документа, у которого шаблонов НЕТ, — на нём и видно, сбросился ли выбор от прежнего типа. */
const EMPTY_TYPE = process.env.SMOKE_EMPTY_TYPE || 'Приказ';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

try {

// ── Шаблоны: выбор привязан к типу документа ──────────────────────────────────
await page.goto(`${BASE}/templates`);
await page.waitForTimeout(3000);

let firstTypeName = '';
await check('templates-auto-select-after-type-pick', async () => {
  const selector = page.locator('button').filter({ hasText: /Тип документа|Выберите тип/ }).first();
  if (!(await selector.count())) throw new Error('селектора типа на странице шаблонов нет');
  await selector.click();
  await page.waitForTimeout(1200);
  const picker = page.locator('[role=dialog]').last();
  const option = picker.locator('button').filter({ hasText: /АОСР|Кабельный журнал|Титульный/ }).first();
  if (!(await option.count())) throw new Error('в пикере типов нет знакомых типов');
  firstTypeName = (await option.innerText()).split(/\r?\n/)[0].trim();
  await option.click();
  await page.waitForTimeout(3000);
  const t = await page.locator('body').innerText();
  if (/Выберите тип документа/.test(t)) throw new Error('тип не выбрался');
  // Шаблон выбирается сам: без этого справа пусто, а слева список — то есть экран «ничего не открыто».
  if (!/версия|Активн|Черновик|Сохранить/i.test(t))
    throw new Error(`шаблон не выбрался сам: ${t.slice(-300)}`);
});

/**
 * Переключение на тип БЕЗ шаблонов: справа обязано быть приглашение, а не редактор.
 *
 * <p>Честно про силу этой проверки: сломанную привязку выбора к типу она НЕ ловит. Снимите метку
 * типа — она всё равно зелёная, потому что обработчик смены типа сбрасывает выбор и сам, а другого
 * пути сменить тип, не размонтировав страницу, у человека нет. Проверено. То есть она сторожит
 * экран, а не замену эффекта; замена проверена чтением.</p>
 */
await check('templates-selection-does-not-leak-to-other-type', async () => {
  const selector = page.locator('button').filter({ hasText: new RegExp(firstTypeName.slice(0, 12)) }).first();
  await selector.click();
  await page.waitForTimeout(1200);
  const picker = page.locator('[role=dialog]').last();
  const empty = picker.locator('button').filter({ hasText: new RegExp(EMPTY_TYPE) }).first();
  if (!(await empty.count())) throw new Error(`типа «${EMPTY_TYPE}» в пикере нет`);
  await empty.click();
  await page.waitForTimeout(3000);
  const t = await page.locator('body').innerText();
  // Утверждение ПОЛОЖИТЕЛЬНОЕ: у типа без шаблонов правая панель обязана предлагать выбрать или
  // создать. Останься там шаблон прежнего типа — вместо приглашения был бы редактор.
  if (!/Выберите шаблон или создайте новый/.test(t))
    throw new Error(`у типа без шаблонов справа не приглашение, а что-то другое: ${t.slice(-300)}`);
});

// ── Комплект: документ по адресу ?doc= ────────────────────────────────────────
await check('set-detail-deep-link-opens-document', async () => {
  await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}?doc=${AOSR_INSTANCE}`);
  await page.waitForSelector('[role=dialog]', { timeout: 20000 })
    .catch(() => { throw new Error('редактор документа по ссылке не открылся'); });
  await page.waitForTimeout(2000);
  const t = await page.locator('[role=dialog]').first().innerText();
  if (!/АОСР/.test(t)) throw new Error(`открылось не то: ${t.slice(0, 200)}`);
});

await check('set-detail-deep-link-survives-reload', async () => {
  // Параметр теперь живёт, пока документ открыт, — значит обновление страницы возвращает туда же.
  await page.reload();
  await page.waitForSelector('[role=dialog]', { timeout: 20000 })
    .catch(() => { throw new Error('после обновления страницы документ не открылся'); });
  await page.waitForTimeout(1500);
  if (!/АОСР/.test(await page.locator('[role=dialog]').first().innerText()))
    throw new Error('после обновления открылось не то');
});

await check('set-detail-closing-clears-doc-param', async () => {
  await page.keyboard.press('Escape');
  await page.waitForTimeout(1500);
  if (new URL(page.url()).searchParams.get('doc'))
    throw new Error(`закрытие не сняло параметр: ${page.url()}`);
});

// ── Сверка: секция из адреса ──────────────────────────────────────────────────
await check('reconciliations-view-param-opens-section', async () => {
  await page.goto(`${BASE}/reconciliations?view=aliases`);
  await page.waitForTimeout(3000);
  const t = await page.locator('body').innerText();
  if (!/Алиас|алиас/i.test(t)) throw new Error(`секция алиасов не открылась: ${t.slice(-300)}`);
});

// ── Диалог источника: подставлен первый кандидат ──────────────────────────────
// Набор системных данных выбран за то, что у него ЕСТЬ свободные кандидаты: у демо-PDF все
// проекции уже добавлены источниками, и подставлять диалогу нечего — проверка была бы пустой.
await check('source-editor-prefills-first-candidate', async () => {
  await page.goto(`${BASE}/datasets`);
  await page.waitForTimeout(2500);
  await page.getByText(SYSTEM_DATASET, { exact: false }).first().click();
  await page.waitForTimeout(2500);
  await page.locator('button').filter({ hasText: /Добавить источник/ }).first().click();
  await page.waitForTimeout(3000);
  const dlg = page.locator('[role=dialog]').last();
  const text = await dlg.innerText();
  if (/— выберите —/.test(text)) throw new Error('кандидат не подставлен — список стоит на плейсхолдере');
  const value = await dlg.locator('input').first().inputValue();
  if (!value.trim()) throw new Error('имя источника не подставлено — поле пусто');
  // Имя подставляется от ПЕРВОГО показанного кандидата: оно обязано его называть.
  if (!text.includes(value.replace(/ \(\d+\)$/, '')))
    throw new Error(`имя «${value}» не отвечает ни одному кандидату в списке`);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(600);
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke экранов с выбором');
