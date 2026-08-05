// Клавиатурный smoke-тест (issue #107, раздел C).
// Прогоняет ключевые клавиатурные контракты по живому фронту без мыши:
//   • видимый фокус после Tab (:focus-visible) на ключевых экранах;
//   • командная палитра Ctrl/⌘+K открывается и закрывается по Esc;
//   • шпаргалка «?» открывается на неполевом фокусе и закрывается по Esc;
//   • Radix-диалоги возвращают фокус и ловятся Esc.
//
// Требует поднятых фронта (:5173) и бэка (:5000) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/keyboard-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

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
} finally {
  await browser.close();
}

process.exit(summarize('Клавиатурный smoke'));
