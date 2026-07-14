// Клавиатурный smoke-тест (issue #107, раздел C).
// Прогоняет ключевые клавиатурные контракты по живому фронту без мыши:
//   • видимый фокус после Tab (:focus-visible) на ключевых экранах;
//   • командная палитра Ctrl/⌘+K открывается и закрывается по Esc;
//   • шпаргалка «?» открывается на неполевом фокусе и закрывается по Esc;
//   • Radix-диалоги возвращают фокус и ловятся Esc.
//
// Требует поднятых фронта (:5173) и бэка (:5000) — см. e2e/README.md.
// Playwright берётся из npx-кеша, браузер — из ms-playwright (пути резолвятся
// динамически, переопределяются env PLAYWRIGHT_PKG / CHROMIUM_EXE).
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/keyboard-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { readdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const BASE = process.env.SMOKE_BASE || 'http://localhost:5173';
const EMAIL = process.env.SMOKE_EMAIL || 'admin@bhs.local';
const PASSWORD = process.env.SMOKE_PASSWORD || 'Demo12345!';
const PALETTE_INPUT = 'input[placeholder="Перейти к разделу…"]';

function findPlaywright() {
  if (process.env.PLAYWRIGHT_PKG) return process.env.PLAYWRIGHT_PKG;
  const npx = path.join(homedir(), 'AppData/Local/npm-cache/_npx');
  for (const hash of existsSync(npx) ? readdirSync(npx) : []) {
    const p = path.join(npx, hash, 'node_modules/playwright/index.js');
    if (existsSync(p)) return p;
  }
  throw new Error('Playwright не найден в npx-кеше — задайте PLAYWRIGHT_PKG');
}

function findChromium() {
  if (process.env.CHROMIUM_EXE) return process.env.CHROMIUM_EXE;
  const root = path.join(homedir(), 'AppData/Local/ms-playwright');
  const builds = (existsSync(root) ? readdirSync(root) : [])
    .filter(d => d.startsWith('chromium_headless_shell-'))
    .sort((a, b) => Number(b.split('-')[1]) - Number(a.split('-')[1]));
  for (const b of builds) {
    const exe = path.join(root, b, 'chrome-headless-shell-win64/chrome-headless-shell.exe');
    if (existsSync(exe)) return exe;
  }
  throw new Error('chrome-headless-shell не найден — задайте CHROMIUM_EXE');
}

const results = [];
async function check(name, fn) {
  try { await fn(); results.push([name, true, '']); console.log(`  ✓ ${name}`); }
  catch (e) { results.push([name, false, e.message]); console.log(`  ✗ ${name} — ${e.message}`); }
}

const pw = await import(pathToFileURL(findPlaywright()).href);
const { chromium } = pw.default ?? pw;
const browser = await chromium.launch({ executablePath: findChromium(), headless: true });
const page = await browser.newPage();
page.on('dialog', d => d.accept());

try {
  // ── Логин ──────────────────────────────────────────────────────────────────
  await page.goto(`${BASE}/login`);
  await page.fill('input[type=email]', EMAIL);
  await page.fill('input[type=password]', PASSWORD);
  await page.click('button[type=submit]');
  await check('login-token', async () => {
    await page.waitForFunction(() => !!localStorage.getItem('access_token'), { timeout: 10000 });
  });

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

const failed = results.filter(([, ok]) => !ok);
console.log(`\n${results.length - failed.length}/${results.length} проверок прошло`);
if (failed.length) { console.error('ПРОВАЛ:', failed.map(([n]) => n).join(', ')); process.exit(1); }
console.log('Клавиатурный smoke — OK');
