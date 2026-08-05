// Общая обвязка живых smoke-прогонов: где взять Playwright и браузер, как войти,
// как считать проверки. Используется keyboard-smoke.mjs и routing-smoke.mjs.
//
// Playwright лежит в npx-кеше (не в node_modules пакета), браузер — в ms-playwright;
// оба пути резолвятся динамически и переопределяются env PLAYWRIGHT_PKG / CHROMIUM_EXE.

import { readdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const BASE = process.env.SMOKE_BASE || 'http://localhost:5173';
export const EMAIL = process.env.SMOKE_EMAIL || 'admin@bhs.local';
export const PASSWORD = process.env.SMOKE_PASSWORD || 'Demo12345!';

export function findPlaywright() {
  if (process.env.PLAYWRIGHT_PKG) return process.env.PLAYWRIGHT_PKG;
  const npx = path.join(homedir(), 'AppData/Local/npm-cache/_npx');
  for (const hash of existsSync(npx) ? readdirSync(npx) : []) {
    const p = path.join(npx, hash, 'node_modules/playwright/index.js');
    if (existsSync(p)) return p;
  }
  throw new Error('Playwright не найден в npx-кеше — задайте PLAYWRIGHT_PKG');
}

export function findChromium() {
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

/** Запускает браузер той сборкой, что реально установлена (версия Playwright обычно ждёт свежее). */
export async function launchBrowser() {
  const pw = await import(pathToFileURL(findPlaywright()).href);
  const { chromium } = pw.default ?? pw;   // пакет CJS — интероп кладёт экспорт в default
  return chromium.launch({ executablePath: findChromium(), headless: true });
}

/**
 * Вход по форме. Ждём именно появления токена: без этого следующий `goto` успевает уйти
 * раньше сохранения, и ProtectedRoute вернёт на /login. Смотрим оба хранилища — «Запомнить
 * меня» выбирает между localStorage и sessionStorage (см. shared/api/token.ts), и прогон
 * не должен зависеть от того, каким это поле стоит по умолчанию.
 */
export async function login(page, email = EMAIL, password = PASSWORD) {
  await page.goto(`${BASE}/login`);
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await page.waitForFunction(
    () => !!(localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token')),
    { timeout: 10000 });
}

/** Выход «изнутри»: чистим оба хранилища, иначе сессия может пережить сброс. */
export async function clearSession(page) {
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
}

/** Счётчик проверок: `check` не роняет прогон на первом провале, `summarize` печатает итог. */
export function createChecks() {
  const results = [];
  async function check(name, fn) {
    try { await fn(); results.push([name, true, '']); console.log(`  ✓ ${name}`); }
    catch (e) { results.push([name, false, e.message]); console.log(`  ✗ ${name} — ${e.message}`); }
  }
  function summarize(title) {
    const failed = results.filter(([, ok]) => !ok);
    console.log(`\n${results.length - failed.length}/${results.length} проверок прошло`);
    if (failed.length) { console.error('ПРОВАЛ:', failed.map(([n]) => n).join(', ')); return 1; }
    console.log(`${title} — OK`);
    return 0;
  }
  return { check, summarize };
}
