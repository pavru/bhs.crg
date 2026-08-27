// Smoke по общим компонентам (issue #858, порция 3).
//
// Modal, ConfirmDialog, TypePicker и командная палитра переписаны на монтирование по открытию, а
// ThemeProvider и DateInput — на производные значения вместо копий, которые переливал эффект. Первые
// четыре уже проходят под четырьмя существующими прогонами (fields, dialogs, keyboard, routing) —
// они открывают эти диалоги десятками. Здесь проверяется то, чего те не касаются:
//   • тема: выбор применяется, переживает перезагрузку и СЛЕДУЕТ ЗА СИСТЕМОЙ (подписка живая);
//   • поле даты: показывает сохранённое значение и отбрасывает незаконченный ввод при потере фокуса.
//
// Тема пишется в localStorage — прогон возвращает её в исходное состояние. Значения полей
// не сохраняются: документ закрывается без сохранения.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/shared-ui-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';
const AOSR = '250701.ЭОМ-1.АОСР';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

const domTheme = () => page.evaluate(() => document.documentElement.getAttribute('data-theme'));
const storedTheme = () => page.evaluate(() => localStorage.getItem('crg-theme'));
const pickTheme = async (label) => {
  await page.getByRole('group', { name: 'Тема оформления' }).getByRole('button', { name: label }).click();
  await page.waitForTimeout(600);
};

await login(page);

try {

// ── Тема ───────────────────────────────────────────────────────────────────────
await page.emulateMedia({ colorScheme: 'light' });
await page.goto(`${BASE}/document-sets`);
await page.waitForTimeout(2000);

await check('theme-choice-applies-to-dom', async () => {
  await pickTheme('Тёмная');
  if ((await domTheme()) !== 'dark') throw new Error(`после выбора «Тёмная» на <html> «${await domTheme()}»`);
  await pickTheme('Светлая');
  if ((await domTheme()) !== 'light') throw new Error(`после выбора «Светлая» на <html> «${await domTheme()}»`);
});

await check('theme-choice-survives-reload', async () => {
  await pickTheme('Тёмная');
  if ((await storedTheme()) !== 'dark') throw new Error(`в localStorage «${await storedTheme()}»`);
  await page.reload();
  await page.waitForTimeout(2000);
  if ((await domTheme()) !== 'dark') throw new Error('после перезагрузки тема не тёмная');
});

// Ради этой проверки и заводился useSyncExternalStore: системная тема меняется БЕЗ участия
// приложения, и подписка обязана быть живой. Эмулируем смену системной настройки браузером.
await check('system-theme-follows-os-change', async () => {
  await pickTheme('Системная');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.waitForTimeout(600);
  if ((await domTheme()) !== 'light') throw new Error(`при светлой системе на <html> «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.waitForTimeout(600);
  if ((await domTheme()) !== 'dark') throw new Error(`система стала тёмной, а на <html> «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'light' });
  await page.waitForTimeout(600);
  if ((await domTheme()) !== 'light') throw new Error('обратная смена системной темы не дошла');
});

// Обратная сторона того же правила: при закреплённой теме системная настройка не должна её
// трогать вовсе. Подписки в этом режиме теперь нет — но отвечает за исход не она, а разрешение
// темы, и проверять надо именно исход.
await check('pinned-theme-ignores-os-change', async () => {
  await pickTheme('Светлая');
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.waitForTimeout(800);
  if ((await domTheme()) !== 'light') throw new Error(`система тёмная перебила закреплённую светлую: «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'light' });
  await page.waitForTimeout(400);
});

await pickTheme('Светлая');   // возвращаем окружение в исходное

// ── Поле даты ──────────────────────────────────────────────────────────────────
await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
await page.waitForSelector('tbody tr', { timeout: 15000 });
await page.waitForTimeout(1000);
await page.getByText(AOSR, { exact: false }).first().click();
await page.waitForSelector('[role=dialog]', { timeout: 15000 });
await page.waitForTimeout(2000);
const editor = page.locator('[role=dialog]').first();
await editor.getByRole('button', { name: /Даты работ/ }).first().click();
await page.waitForTimeout(1200);

// Сегменты поля даты адресуем по их собственным плейсхолдерам, а не «любой input с четырьмя
// цифрами»: под ту примету попадёт и обычное числовое поле, и проверка покраснела бы на исправном
// компоненте, ткнув в чужое поле (поймано ревью PR #863).
const yearSegs = editor.locator('input[placeholder="ГГГГ"]');

await check('date-input-shows-stored-value', async () => {
  if ((await yearSegs.count()) < 1) throw new Error('на разделе «Даты работ» нет полей даты');
  const values = await yearSegs.evaluateAll(els => els.map(e => e.value));
  if (!values.some(v => /^\d{4}$/.test(v)))
    throw new Error(`ни одно поле даты не показывает сохранённый год: ${JSON.stringify(values)}`);
});

await check('date-input-discards-partial-input-on-blur', async () => {
  // Берём ГОД: незаконченные день и месяц при потере фокуса дополняются нулём (blurD/blurM) и
  // уезжают в значение, и на них откат был бы неотличим от дополнения.
  const values = await yearSegs.evaluateAll(els => els.map(e => e.value));
  const idx = values.findIndex(v => /^\d{4}$/.test(v));
  if (idx < 0) throw new Error('нет заполненного года — нечего откатывать');
  const seg = yearSegs.nth(idx);
  const before = await seg.inputValue();
  await seg.click();
  await seg.fill('20');   // два разряда из четырёх — значение так не соберётся
  await page.waitForTimeout(300);
  if ((await seg.inputValue()) !== '20') throw new Error('набранное не показывается, пока поле в фокусе');
  // Уводим фокус, не закрывая редактор: щелчок по заголовку раздела.
  await editor.getByRole('button', { name: /Даты работ/ }).first().click();
  await page.waitForTimeout(900);
  const after = await yearSegs.nth(idx).inputValue();
  if (after !== before) throw new Error(`после потери фокуса в сегменте «${after}», а в значении «${before}»`);
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke общих компонентов');
