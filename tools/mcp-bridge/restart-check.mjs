#!/usr/bin/env node
/**
 * Проверка живучести моста (issue #438): пережить перезапуск приложения БЕЗ перезапуска себя.
 *
 * Случай не редкий, а обычный: при разработке приложение перезапускается постоянно, а мост клиент
 * поднимает один раз за сессию. Отравленный мёртвой сессией мост выглядит для агента так, будто
 * инструментов не стало вовсе — именно так это и проявилось в работе.
 *
 * Запуск (приложение должно быть поднято):
 *   BHS_EMAIL=… BHS_PASSWORD=… node tools/mcp-bridge/restart-check.mjs
 */
import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const BASE = process.env.BHS_URL ?? 'http://localhost:5000';

const ps = (script) => execFileSync('powershell', ['-NoProfile', '-Command', script], { encoding: 'utf8' });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function apiReachable() {
  try { await fetch(`${BASE}/api/health`); return true; } catch { return false; }
}

async function waitForApi(timeoutMs = 120_000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    if (await apiReachable()) return true;
    await sleep(2000);
  }
  return false;
}

const bridge = spawn('node', [join(HERE, 'bridge.mjs')], {
  cwd: ROOT, env: process.env, stdio: ['pipe', 'pipe', 'inherit'],
});

const pending = new Map();
createInterface({ input: bridge.stdout }).on('line', line => {
  let msg; try { msg = JSON.parse(line); } catch { return; }
  const resolve = pending.get(msg.id);
  if (resolve) { pending.delete(msg.id); resolve(msg); }
});

const send = obj => bridge.stdin.write(JSON.stringify(obj) + '\n');
const ask = (id, method, params = {}) => new Promise((resolve, reject) => {
  pending.set(id, resolve);
  send({ jsonrpc: '2.0', id, method, params });
  setTimeout(() => reject(new Error(`нет ответа на ${method}`)), 90_000);
});

let failed = false;
const check = (ok, text) => { if (!ok) failed = true; console.log(`${ok ? 'OK  ' : 'СБОЙ'}  ${text}`); };

send({ jsonrpc: '2.0', id: 1, method: 'initialize',
  params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'restart-check', version: '1' } } });
send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });

const before = await ask(2, 'tools/list');
const beforeCount = before.result?.tools?.length ?? 0;
check(beforeCount > 0, `до перезапуска инструментов: ${beforeCount}`);

console.log('останавливаю приложение…');
ps("Get-Process -Name BHS.CRG.Api -EA SilentlyContinue | Stop-Process -Force; " +
   "Get-Process -Name dotnet -EA SilentlyContinue | Where-Object { $_.Path -like '*BHS.CRG*' } | Stop-Process -Force");
await sleep(2000);

// Пока приложение лежит, агенту нужна внятная причина, а не «fetch failed».
const down = await ask(3, 'tools/list');
check(/недоступен/.test(down.error?.message ?? ''), `при лежащем приложении: ${down.error?.message?.slice(0, 60)}`);

console.log('поднимаю приложение…');
// Start-Process сам не блокирует, поэтому синхронный вызов здесь уместен: отдельный detached-spawn
// на Windows молча не доносил аргументы до powershell и приложение не поднималось.
ps(`Set-Location '${ROOT}'; Start-Process -FilePath 'dotnet' -ArgumentList ` +
   `'run','--project','src/server/BHS.CRG.Api','--no-launch-profile' -WindowStyle Hidden`);
check(await waitForApi(), 'приложение поднялось');

// Тот же мост, без перезапуска: обязан сам переиграть рукопожатие.
const after = await ask(4, 'tools/list');
const afterCount = after.result?.tools?.length ?? 0;
check(afterCount === beforeCount, `после перезапуска тем же мостом: ${afterCount} (было ${beforeCount})`);

bridge.stdin.end();
process.exitCode = failed ? 1 : 0;
console.log(failed ? '\nПРОВЕРКА НЕ ПРОЙДЕНА' : '\nПроверка пройдена');
