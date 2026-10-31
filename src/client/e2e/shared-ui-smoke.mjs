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

import { BASE, launchBrowser, login, submitLogin, clearSession, createChecks, settled, until, accountAction, emulateTouch, pickTheme as pickThemeOf } from './harness.mjs';

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
// Тема — из меню профиля в шапке (issue #1430).
const pickTheme = label => pickThemeOf(page, label);

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
  await settled(page);
  await pickTheme('Авто');

  await clearSession(page);
  await page.evaluate(() => localStorage.removeItem('crg-theme'));
  // Сначала вход — он сам открывает страницу входа заново и ждёт токен; потом — всё, что вход
  // потянул за собой.
  await login(page);
  await settled(page);

  await pickTheme('Тёмная');
  await page.evaluate(() => localStorage.removeItem('crg-theme'));
  await page.reload();
  await settled(page);
  if ((await domTheme()) !== 'dark')
    throw new Error(`выбор темы сразу после входа не доехал до сервера: на <html> «${await domTheme()}»`);
});

// ── Тема ───────────────────────────────────────────────────────────────────────
await page.emulateMedia({ colorScheme: 'light' });
await page.goto(`${BASE}/document-sets`);
await settled(page);

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
  await settled(page);
  if ((await domTheme()) !== 'dark') throw new Error('после перезагрузки тема не тёмная');
});

// Ради этой проверки и заводился useSyncExternalStore: системная тема меняется БЕЗ участия
// приложения, и подписка обязана быть живой. Эмулируем смену системной настройки браузером.
await check('system-theme-follows-os-change', async () => {
  await pickTheme('Авто');
  await page.emulateMedia({ colorScheme: 'light' });
  await settled(page);
  if ((await domTheme()) !== 'light') throw new Error(`при светлой системе на <html> «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'dark' });
  await settled(page);
  if ((await domTheme()) !== 'dark') throw new Error(`система стала тёмной, а на <html> «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'light' });
  await settled(page);
  if ((await domTheme()) !== 'light') throw new Error('обратная смена системной темы не дошла');
});

// Обратная сторона того же правила: при закреплённой теме системная настройка не должна её
// трогать вовсе. Подписки в этом режиме теперь нет — но отвечает за исход не она, а разрешение
// темы, и проверять надо именно исход.
await check('pinned-theme-ignores-os-change', async () => {
  await pickTheme('Светлая');
  await page.emulateMedia({ colorScheme: 'dark' });
  await settled(page);
  if ((await domTheme()) !== 'light') throw new Error(`система тёмная перебила закреплённую светлую: «${await domTheme()}»`);
  await page.emulateMedia({ colorScheme: 'light' });
  await settled(page);
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
  await settled(page);
  if ((await domTheme()) !== 'dark')
    throw new Error(`без зеркала тема не приехала с сервера: на <html> «${await domTheme()}»`);
});

// Настройки уходят ВМЕСТЕ с сессией. Кэш ответов переживал выход из системы, и следующий
// вошедший в той же вкладке получал чужую тему и чужой язык — без единого запроса к серверу, то
// есть без всякого признака подмены (ревью PR #999). На общем компьютере это обычное дело:
// «выйти — войти другим» занимает пять секунд, а кэш жил пять минут.
await check('settings-do-not-survive-a-change-of-user', async () => {
  await pickTheme('Тёмная');
  await accountAction(page, 'Выйти');
  await page.waitForSelector('input[type=email]', { timeout: 10000 });

  // Вход ДРУГИМ человеком в той же вкладке — без перезагрузки страницы.
  // Утверждение ниже — «тема НЕ тёмная», и читать его можно только после того, как настройки
  // второго человека доехали: до них на экране ещё тема страницы входа, и это не поломка.
  await submitLogin(page, OTHER_EMAIL, OTHER_PASSWORD);
  await settled(page);

  if ((await domTheme()) === 'dark')
    throw new Error('вошедшему второму человеку досталась тема первого');

  // Возвращаем прогон к администратору: дальше нужны его данные.
  await accountAction(page, 'Выйти');
  await page.waitForSelector('input[type=email]', { timeout: 10000 });
});

await login(page);
await page.goto(`${BASE}/document-sets`);
await settled(page);

await pickTheme('Светлая');   // возвращаем окружение в исходное

// ── Цель касания 48 ([UI-68], sharedui-md3-10) ──────────────────────────────────
// Видимая кнопка-иконка — 40, а касание в 4 px за краем достаётся ей: утилита touch-target под
// pointer: coarse. Ширина экрана условием не служит, поэтому — рабочий стол с касанием.
await check('touch-target-reaches-beyond-icon-button', async () => {
  await emulateTouch(page);
  try {
    const help = page.getByRole('button', { name: 'Справка и горячие клавиши' });
    await help.waitFor();
    const hit = await help.evaluate(el => {
      const box = el.getBoundingClientRect();
      const at = (x, y) => document.elementFromPoint(x, y)?.closest('button') === el;
      return {
        size: [Math.round(box.width), Math.round(box.height)],
        left: at(box.left - 4 + 0.5, box.top + box.height / 2),
        top: at(box.left + box.width / 2, box.top - 4 + 0.5),
      };
    });
    if (hit.size.join('×') !== '40×40') throw new Error(`видимая кнопка-иконка ${hit.size.join('×')}, а не 40×40`);
    if (!hit.left || !hit.top) throw new Error(`касание в 4 px за краем не достаётся кнопке: слева ${hit.left}, сверху ${hit.top}`);
  } finally {
    await emulateTouch(page, false);
  }
});

// ── Поле даты ──────────────────────────────────────────────────────────────────
await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
await page.waitForSelector('tbody tr', { timeout: 15000 });
await settled(page);
await page.getByText(AOSR, { exact: false }).first().click();
// Документ — страница (issue #1384): ждём её, а не окно.
const editor = page.getByTestId('document-editor');
await editor.waitFor({ timeout: 15000 });
await settled(page);
await editor.getByRole('button', { name: /Даты работ/ }).first().click();
await settled(page);

// Сегменты поля даты адресуем по их собственному имени («год», issue #1463), а не «любой input с
// четырьмя цифрами»: под ту примету попадёт и обычное числовое поле, и проверка покраснела бы на
// исправном компоненте, ткнув в чужое поле (поймано ревью PR #863). Не по подсказке «ГГГГ»: она —
// слово языка, а имя сторожит, что поле названо.
const yearSegs = editor.getByRole('textbox', { name: 'год', exact: true });

await check('date-input-shows-stored-value', async () => {
  if ((await yearSegs.count()) < 1) throw new Error('на разделе «Даты работ» нет полей даты');
  const values = await yearSegs.evaluateAll(els => els.map(e => e.value));
  if (!values.some(v => /^\d{4}$/.test(v)))
    throw new Error(`ни одно поле даты не показывает сохранённый год: ${JSON.stringify(values)}`);
});

await check('date-input-keeps-partial-input-with-error-on-blur', async () => {
  // Недописанная дата при уходе из поля ОСТАЁТСЯ видимой, с ошибкой, и не сохраняется (issue #1460,
  // решение владельца). Раньше она молча откатывалась к прежней, и человек считал ввод принятым.
  // Берём ГОД, и три разряда: день и месяц из одной цифры при уходе дополняются нулём (blurD/blurM),
  // а двузначный год становится 20ГГ — ни то ни другое недописанным не останется.
  const values = await yearSegs.evaluateAll(els => els.map(e => e.value));
  const idx = values.findIndex(v => /^\d{4}$/.test(v));
  if (idx < 0) throw new Error('нет заполненного года — нечего проверять');
  const seg = yearSegs.nth(idx);
  const before = await seg.inputValue();
  await seg.click();
  await seg.fill('202');
  await settled(page);
  // Уводим фокус, не закрывая редактор: щелчок по заголовку раздела. Набранное снимается ТАЙМЕРОМ
  // (120 мс — поле ждёт, не вернётся ли фокус в соседний сегмент), а таймеров `settled()` не видит:
  // ждём саму пометку ошибки.
  await editor.getByRole('button', { name: /Даты работ/ }).first().click();
  await until(async () => {
    if ((await seg.getAttribute('aria-invalid')) !== 'true') throw new Error('поле с недописанной датой не помечено ошибкой');
  });
  const after = await seg.inputValue();
  if (after !== '202') throw new Error(`после ухода в сегменте «${after}» вместо набранного «202» — ввод откатился молча`);
  if (!(await editor.getByText('Дата не дописана').first().isVisible().catch(() => false)))
    throw new Error('под полем нет ошибки «Дата не дописана»');
  // Esc в поле возвращает прежнее значение и остаётся в поле.
  await seg.click();
  await page.keyboard.press('Escape');
  await until(async () => {
    const back = await seg.inputValue();
    if (back !== before) throw new Error(`Esc не вернул прежнее: в сегменте «${back}», было «${before}»`);
  });
  // Редактор — страница, Esc её не закрыл бы и так. Признак того, что Esc достался полю, — фокус
  // остался в сегменте: окно на этом месте увело бы его к выходу (это сторожит invoice-payment-smoke).
  const at = await page.evaluate(() => document.activeElement?.getAttribute('aria-label'));
  if (at !== 'год') throw new Error(`после Esc фокус не в поле даты, а на «${at}»`);
});

await check('date-calendar-has-today-and-clear-returns-by-esc', async () => {
  // Строка действий календаря (issue #1463, вариант В): «Сегодня» есть всегда и называет дату. У
  // необязательного поля есть «Очистить»: дата стирается, фокус — в первом сегменте, Esc возвращает её.
  // Обязательное поле «Очистить» не показывает — тогда проверяется только «Сегодня».
  const values = await yearSegs.evaluateAll(els => els.map(e => e.value));
  const idx = values.findIndex(v => /^\d{4}$/.test(v));
  if (idx < 0) throw new Error('нет заполненного года — нечего проверять');
  const seg = yearSegs.nth(idx);
  const before = await seg.inputValue();
  const field = seg.locator('xpath=ancestor::*[@role="group"][1]');
  await field.getByRole('button', { name: 'Открыть календарь' }).click();
  const today = page.getByRole('button', { name: /^Сегодня, \d/ });
  await today.waitFor({ timeout: 5000 });
  const clear = page.getByRole('button', { name: 'Очистить', exact: true });
  if (!(await clear.isVisible().catch(() => false))) { await page.keyboard.press('Escape'); return; }
  // Первый сегмент — по полю, а не словом «день»: у точности «месяц» и у en-US первым идёт месяц.
  const first = await field.locator('input').first().getAttribute('aria-label');
  await clear.click();
  await until(async () => {
    if ((await seg.inputValue()) !== '') throw new Error('«Очистить» не стёрло год');
    const at = await page.evaluate(() => document.activeElement?.getAttribute('aria-label'));
    if (at !== first) throw new Error(`после «Очистить» фокус не в первом сегменте («${first}»), а на «${at}»`);
  });
  await page.keyboard.press('Escape');
  await until(async () => {
    const back = await seg.inputValue();
    if (back !== before) throw new Error(`Esc после очистки не вернул дату: год «${back}», было «${before}»`);
  });
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke общих компонентов');
