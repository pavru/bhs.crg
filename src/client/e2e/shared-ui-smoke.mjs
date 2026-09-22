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

import { BASE, EMAIL, PASSWORD, launchBrowser, login, clearSession, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';
const AOSR = '250701.ЭОМ-1.АОСР';
// Второй человек — для проверки «настройки уходят вместе с сессией». Значения те же, что у посева
// (e2e/seed.mjs): отдельного источника заводить незачем, а разойдясь, они дали бы «не вошёл».
const OTHER_EMAIL = process.env.SMOKE_USER_EMAIL || 'petrov@bhs.local';
const OTHER_PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';

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

// ── Настройки сразу после входа ────────────────────────────────────────────────
//
// Вход через форму страницу НЕ перезагружает: приложение продолжает работать тем же деревом, что
// рисовало страницу входа. Обработчик выбора темы, розданный потребителям ДО входа, помнил бы
// «никто не вошёл» — и первое нажатие темнило бы экран, не отправив ничего на сервер: молча, до
// первой перезагрузки, которая всё и откатит. Каждая проверка ниже идёт после page.goto, то есть
// по перезагруженному дереву, и этого пути не касается вовсе (найдено ревью PR #999).
//
// ⚠️ Подготовка обязательна и стоит трёх шагов — без неё проверка проходит, ничего не проверяя
// (так и вышло с первого раза). Протухшее замыкание помнит «никто не вошёл» ТОЛЬКО если дерево
// загрузилось без токена, и срабатывает это ТОЛЬКО когда вход не меняет показанного значения:
// изменись оно — пересчитается мемоизация, а с ней и замыкание. Поэтому:
//   1) тема в учётной записи доводится до «как в системе» — по перезагруженному дереву, наверняка;
//   2) зеркало и сессия стираются, страница входа открывается ЗАНОВО (дерево без токена);
//   3) вход через форму, и первое же нажатие — то самое, вокруг которого всё и строится.
await check('theme-saves-right-after-form-login', async () => {
  await page.goto(`${BASE}/document-sets`);
  await page.waitForTimeout(2000);
  await pickTheme('Системная');

  await clearSession(page);
  await page.evaluate(() => localStorage.removeItem('crg-theme'));
  await page.goto(`${BASE}/login`);
  await page.fill('input[type=email]', EMAIL);
  await page.fill('input[type=password]', PASSWORD);
  await page.click('button[type=submit]');
  await page.waitForTimeout(2000);

  await pickTheme('Тёмная');
  await page.evaluate(() => localStorage.removeItem('crg-theme'));
  await page.reload();
  await page.waitForTimeout(2500);
  if ((await domTheme()) !== 'dark')
    throw new Error(`выбор темы сразу после входа не доехал до сервера: на <html> «${await domTheme()}»`);
});

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

// Тема хранится на СЕРВЕРЕ (issue #953, ТЗ CORE-25.3), а в браузере остаётся только зеркало.
// Проверяем это единственным честным способом: убираем зеркало — то есть делаем из этой машины
// «другую» — и ждём, что выбор вернётся. Пока тема жила в localStorage, проверка «переживает
// перезагрузку» проходила бы и здесь: она читала то же самое хранилище, куда сама и писала.
//
// Чистим ровно один ключ, а не хранилище целиком: там же лежит токен входа, и полная очистка
// проверяла бы не настройки, а страницу входа.
await check('theme-comes-from-the-server-not-the-browser', async () => {
  await pickTheme('Тёмная');
  await page.evaluate(() => localStorage.removeItem('crg-theme'));
  if ((await storedTheme()) !== null) throw new Error('зеркало темы не удалилось — проверка ничего не значит');
  await page.reload();
  await page.waitForTimeout(2500);
  if ((await domTheme()) !== 'dark')
    throw new Error(`без зеркала тема не приехала с сервера: на <html> «${await domTheme()}»`);
});

// Настройки уходят ВМЕСТЕ с сессией. Кэш ответов переживал выход из системы, и следующий
// вошедший в той же вкладке получал чужую тему и чужой язык — без единого запроса к серверу, то
// есть без всякого признака подмены (ревью PR #999). На общем компьютере это обычное дело:
// «выйти — войти другим» занимает пять секунд, а кэш жил пять минут.
await check('settings-do-not-survive-a-change-of-user', async () => {
  await pickTheme('Тёмная');
  await page.getByRole('button', { name: 'Выйти' }).click();
  await page.waitForSelector('input[type=email]', { timeout: 10000 });

  // Вход ДРУГИМ человеком в той же вкладке — без перезагрузки страницы.
  await page.fill('input[type=email]', OTHER_EMAIL);
  await page.fill('input[type=password]', OTHER_PASSWORD);
  await page.click('button[type=submit]');
  await page.waitForTimeout(2500);

  if ((await domTheme()) === 'dark')
    throw new Error('вошедшему второму человеку досталась тема первого');

  // Возвращаем прогон к администратору: дальше нужны его данные.
  await page.getByRole('button', { name: 'Выйти' }).click();
  await page.waitForSelector('input[type=email]', { timeout: 10000 });
});

await login(page);
await page.goto(`${BASE}/document-sets`);
await page.waitForTimeout(2000);

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
