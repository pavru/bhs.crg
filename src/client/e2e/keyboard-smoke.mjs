// Клавиатурный smoke-тест (issue #107, раздел C).
// Прогоняет ключевые клавиатурные контракты по живому фронту без мыши:
//   • видимый фокус после Tab (:focus-visible) на ключевых экранах;
//   • командная палитра Ctrl/⌘+K открывается и закрывается по Esc;
//   • шпаргалка «?» открывается на неполевом фокусе и закрывается по Esc;
//   • Radix-диалоги возвращают фокус и ловятся Esc;
//   • чем закрывается модальный диалог — по его состоянию (issue #1235).
//
// Требует поднятых фронта (:5173) и бэка (:5000) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/keyboard-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, until } from './harness.mjs';

const PALETTE_INPUT = 'input[placeholder="Перейти к разделу…"]';
const { check, summarize } = createChecks();

const browser = await launchBrowser();
const page = await browser.newPage();
page.on('dialog', d => d.accept());

try {
  // ── Логин ──────────────────────────────────────────────────────────────────
  await check('login-token', () => login(page));

  // ── Видимый фокус после Tab на ключевых экранах ──────────────────────────────
  for (const route of ['/', '/document-sets', '/common-data', '/quality-docs', '/settings']) {
    await page.goto(`${BASE}${route}`);
    await page.waitForLoadState('networkidle');
    await check(`tab-focus-visible ${route}`, async () => {
      await page.evaluate(() => document.activeElement instanceof HTMLElement && document.activeElement.blur());
      await page.keyboard.press('Tab');
      const ok = await page.evaluate(() => {
        const el = document.activeElement;
        return !!el && el !== document.body && el.matches(':focus-visible');
      });
      if (!ok) throw new Error('после Tab активный элемент не :focus-visible');
    });
  }

  // ── Командная палитра Ctrl+K ────────────────────────────────────────────────
  await page.goto(`${BASE}/`);
  await page.waitForLoadState('networkidle');
  await check('ctrl-k-opens-palette', async () => {
    await page.keyboard.press('Control+k');
    await page.waitForSelector(PALETTE_INPUT, { state: 'visible', timeout: 3000 });
  });
  await check('palette-input-focused', async () => {
    const focused = await page.evaluate(sel => document.activeElement === document.querySelector(sel), PALETTE_INPUT);
    if (!focused) throw new Error('поле палитры не получило фокус при открытии');
  });
  await check('esc-closes-palette', async () => {
    await page.keyboard.press('Escape');
    await page.waitForSelector(PALETTE_INPUT, { state: 'hidden', timeout: 3000 });
  });

  // ── Шпаргалка «?» ───────────────────────────────────────────────────────────
  await check('question-opens-help', async () => {
    await page.evaluate(() => document.activeElement instanceof HTMLElement && document.activeElement.blur());
    await page.keyboard.press('?');
    await page.getByText('Горячие клавиши').first().waitFor({ state: 'visible', timeout: 3000 });
  });
  await check('esc-closes-help', async () => {
    await page.keyboard.press('Escape');
    await page.getByText('Горячие клавиши').first().waitFor({ state: 'hidden', timeout: 3000 });
  });

  // ── Чем закрывается диалог (issue #1235) ────────────────────────────────────
  // Правило — по состоянию окна. Сама таблица проверена тестом `Modal`; здесь то, чего тест не
  // видит: настоящий щелчок по затемнению, настоящий фокус и слой, вложенный в диалог.
  const dialogs = () => page.locator('[role=dialog]').count();
  // Щелчок — только по затихшей странице. Слой Radix начинает слушать нажатия не в момент
  // появления окна, а таймером после него: щелчок, пришедший в этот зазор, окно не видит вовсе.
  // На дев-стенде зазор не ловится, в CI поймался — «лёгкий диалог не закрылся» при исправном коде.
  const outside = async () => { await settled(page); await page.mouse.click(6, 400); };
  const focusedText = () => page.evaluate(() => (document.activeElement?.textContent ?? '').trim());
  const hinted = () => page.evaluate(() => (document.querySelector('[data-modal-hint]')?.textContent ?? '').trim());
  const closed = () => until(async () => { if (await dialogs()) throw new Error('диалог не закрылся'); });
  const openBugReport = async () => {
    await page.getByText('Сообщить об ошибке', { exact: true }).first().click();
    await page.locator('[role=dialog] textarea').first().waitFor({ state: 'visible', timeout: 5000 });
  };

  await check('light-dialog-closes-on-outside-click', async () => {
    await page.evaluate(() => document.activeElement instanceof HTMLElement && document.activeElement.blur());
    await page.keyboard.press('?');
    await page.getByText('Горячие клавиши').first().waitFor({ state: 'visible', timeout: 3000 });
    await outside();
    await closed();
  });

  await check('empty-form-survives-outside-click-and-shows-exit', async () => {
    await openBugReport();
    await outside();
    await until(async () => { if ((await hinted()) !== 'Отмена') throw new Error('кнопка выхода не обведена'); });
    if (!(await dialogs())) throw new Error('пустая форма закрылась щелчком мимо');
  });

  await check('empty-form-closes-on-escape', async () => {
    await page.keyboard.press('Escape');
    await closed();
  });

  await check('typed-form-survives-outside-click-and-escape', async () => {
    await openBugReport();
    const field = page.locator('[role=dialog] textarea').first();
    await field.fill('набранное, которое жалко');
    await outside();
    await settled(page);
    if (!(await dialogs())) throw new Error('форма с набранным закрылась щелчком мимо');
    // Щелчок мимо курсор из поля не выносит: следующий пробел не должен стать «Отменой».
    if (!(await field.evaluate(el => el === document.activeElement))) throw new Error('щелчок мимо увёл фокус из поля');
    await page.keyboard.press('Escape');
    await until(async () => { if ((await focusedText()) !== 'Отмена') throw new Error('Esc не перенёс фокус на «Отмену»'); });
    if (!(await dialogs())) throw new Error('форма с набранным закрылась по Esc');
  });

  await check('exit-of-typed-form-asks-and-escape-continues', async () => {
    // Весь путь — с клавиатуры, без мыши: вопрос обязан забрать фокус, иначе на него не ответить.
    const question = page.getByText('Закрыть без сохранения?');
    await page.keyboard.press('Enter');   // фокус на «Отмене» — выход идёт той же охраной, что крестик
    await question.waitFor({ state: 'visible', timeout: 3000 });
    await until(async () => {
      if ((await focusedText()) !== 'Продолжить редактирование') throw new Error('вопрос не забрал фокус на безопасный ответ');
    });
    await page.keyboard.press('Escape');  // во внутреннем вопросе Esc — «Продолжить редактирование»
    await question.waitFor({ state: 'hidden', timeout: 3000 });
    if ((await page.locator('[role=dialog] textarea').first().inputValue()) !== 'набранное, которое жалко')
      throw new Error('набранное не уцелело');
    await until(async () => { if ((await focusedText()) !== 'Отмена') throw new Error('фокус не вернулся на «Отмену»'); });
    await page.keyboard.press('Enter');
    await question.waitFor({ state: 'visible', timeout: 3000 });
    await page.keyboard.press('Tab');     // с «Продолжить» на «Закрыть»
    await page.keyboard.press('Enter');
    await closed();
  });

  // Слой внутри диалога. Календарь и диалог — разные пакеты Radix; пока у них были разные копии
  // общей стопки слоёв, оба считали себя верхними, и Esc закрывал сразу оба.
  await check('escape-in-nested-calendar-closes-only-it', async () => {
    await page.goto(`${BASE}/periods`);
    await page.waitForLoadState('networkidle');
    await page.getByRole('button', { name: 'Закрыть период' }).first().click();
    const dlg = page.locator('[role=dialog]').last();
    await dlg.getByRole('button', { name: /календар/i }).last().click();
    const calendar = page.locator('[data-radix-popper-content-wrapper]');
    await calendar.waitFor({ state: 'visible', timeout: 3000 });
    await page.keyboard.press('Escape');
    await calendar.waitFor({ state: 'hidden', timeout: 3000 });
    await settled(page);
    if (!(await dialogs())) throw new Error('Esc в календаре закрыл и диалог');
    await page.keyboard.press('Escape');   // в самом диалоге ничего не набрано — теперь закрывается он
    await closed();
  });

  // Палитра поверх окна с правками (issue #1238): её команда увела бы со страницы вместе с окном —
  // правило закрытия обходилось двумя нажатиями. Вместо палитры окно показывает свой выход.
  await check('palette-does-not-open-over-dirty-dialog', async () => {
    await page.goto(`${BASE}/users`);
    await page.waitForLoadState('networkidle');
    await page.getByRole('button', { name: /Добавить пользователя/ }).click();
    const dlg = page.locator('[role=dialog]').last();
    await dlg.getByLabel(/Email/i).first().fill('palette-probe@bhs.local');
    await page.keyboard.press('Control+k');
    await dlg.locator('[data-modal-hint]').waitFor({ state: 'attached', timeout: 2000 });
    if (await page.locator(PALETTE_INPUT).count()) throw new Error('палитра открылась поверх окна с правками');
    if (await dialogs() !== 1) throw new Error(`ожидалось одно окно, открыто ${await dialogs()}`);
  });
} finally {
  await browser.close();
}

// Код возврата, а не process.exit(): тот обрывает недописанный stdout, и при перенаправлении
// вывода в файл последние строки итога теряются.
process.exitCode = summarize('Клавиатурный smoke');
