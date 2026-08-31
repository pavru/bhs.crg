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

/// Именно /api/version: он анонимный, не ходит в базу и им же проверяют готовность compose,
/// update.sh и посев прогонов. Прежний /api/health в приложении не существует вовсе (здоровье живёт
/// на /api/notifications/health, и то за авторизацией), а код ответа не проверялся — значит проба
/// отвечала на вопрос «кто-нибудь слушает порт», а не «поднято ли БЕЗ приложение». Чужой слушатель
/// на 5000 проходил бы её насквозь, и проверка снова обвинила бы мост.
async function apiReachable() {
  try { return (await fetch(`${BASE}/api/version`)).ok; } catch { return false; }
}

async function waitForApi(timeoutMs = 120_000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    if (await apiReachable()) return true;
    await sleep(2000);
  }
  return false;
}

// Предусловия, а не первые проверки: не хватает чего-то снаружи — так и надо сказать. С лежащим
// приложением мост честно отдаёт ноль инструментов, и проверка объявляла бы сломанным ЕГО.
if (!process.env.BHS_EMAIL || !process.env.BHS_PASSWORD) {
  // Без них мост выходит сразу же (bridge.mjs), и выглядело это так: скрипт ждал ответа девяносто
  // секунд и падал на «нет ответа на tools/list», ни словом не помянув переменные.
  console.log('Не заданы BHS_EMAIL и BHS_PASSWORD — мосту нечем войти в приложение.');
  process.exit(2);
}
if (!await apiReachable()) {
  console.log(`Приложение не отвечает на ${BASE} — проверять нечего.`);
  console.log('Поднимите его (dotnet run --project src/server/BHS.CRG.Api) и повторите.');
  process.exit(2);
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

// Мост может умереть и посреди прогона — например, если приложение перестало принимать его пароль.
// Ждать после этого девяносто секунд бессмысленно: отвечать уже некому, и сказать надо сразу.
const orphaned = [];
bridge.on('exit', code => {
  const err = new Error(`мост завершился с кодом ${code} — отвечать больше некому`);
  for (const reject of orphaned.splice(0)) reject(err);
});

const send = obj => bridge.stdin.write(JSON.stringify(obj) + '\n');
const ask = (id, method, params = {}) => new Promise((resolve, reject) => {
  orphaned.push(reject);
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
//
// Профиль назван ЯВНО, и раньше он был отключён (--no-launch-profile) — из-за этого проверка не
// проходила никогда.
//
// Дело не в рабочем каталоге: корень содержимого и без профиля указывает на каталог проекта, и
// appsettings.json находится. Но в нём строка подключения ПУСТА — настоящая лежит в
// appsettings.Development.json, а он подмешивается только когда среда равна Development. Профиль
// её и задаёт; без профиля её не задаёт никто, и StorageConfigGuard честно обрывает старт словами
// «Строка подключения не задана». Проверено обратным: с --no-launch-profile и вручную выставленной
// ASPNETCORE_ENVIRONMENT=Development приложение поднимается.
//
// Краснела при этом строка «приложение поднялось», а следом «после перезапуска тем же мостом: 0» —
// то есть проверка сообщала о поломке МОСТА там, где не поднялось приложение.
//
// -lp http, а не «первый попавшийся»: профилей два, и порядок в launchSettings.json меняется от
// одного движения в IDE — а второй профиль слушает ещё и https-порт. --urls держит адрес тем же,
// что опрашивает скрипт: иначе BHS_URL соблюдался бы наполовину — опрос по нему, а приложение по
// адресу из профиля.
ps(`Set-Location '${ROOT}'; Start-Process -FilePath 'dotnet' -ArgumentList ` +
   `'run','--project','src/server/BHS.CRG.Api','-lp','http','--urls','${BASE}' -WindowStyle Hidden`);
check(await waitForApi(), 'приложение поднялось');

// Тот же мост, без перезапуска: обязан сам переиграть рукопожатие.
const after = await ask(4, 'tools/list');
const afterCount = after.result?.tools?.length ?? 0;
check(afterCount === beforeCount, `после перезапуска тем же мостом: ${afterCount} (было ${beforeCount})`);

bridge.stdin.end();
process.exitCode = failed ? 1 : 0;
console.log(failed ? '\nПРОВЕРКА НЕ ПРОЙДЕНА' : '\nПроверка пройдена');
