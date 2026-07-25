#!/usr/bin/env node
/**
 * Мост stdio ↔ HTTP для MCP-сервера BHS.CRG (issue #415).
 *
 * Зачем: MCP-сервер приложения живёт по HTTP и требует JWT, а access-токен живёт всего час
 * (AccessTokenMinutes=60, продление задумано через refresh). Статичный заголовок в конфиге клиента
 * обновляться не умеет — токен пришлось бы перевставлять руками каждый час. Мост логинится сам и
 * держит токен свежим, поэтому в конфиге лежат только адрес и учётные данные.
 *
 * Клиент общается с мостом по stdio (построчный JSON-RPC), мост — с приложением по HTTP.
 *
 * ВАЖНО: в stdout уходит ТОЛЬКО протокол. Всё диагностическое — в stderr, иначе клиент получит
 * мусор вместо JSON-RPC и разорвёт соединение.
 */

import { createInterface } from 'node:readline';

const BASE = (process.env.BHS_URL ?? 'http://localhost:5000').replace(/\/+$/, '');
const MCP_URL = `${BASE}/mcp`;
const EMAIL = process.env.BHS_EMAIL;
const PASSWORD = process.env.BHS_PASSWORD;
/** Обновляем токен заранее: если ждать 401, первый же запрос после истечения стоил бы лишнего круга. */
const REFRESH_BEFORE_EXPIRY_MS = 5 * 60 * 1000;

if (!EMAIL || !PASSWORD) {
  console.error('[bhs-mcp] Не заданы BHS_EMAIL и BHS_PASSWORD — мост не может войти в приложение.');
  process.exit(1);
}

const log = (...a) => console.error('[bhs-mcp]', ...a);

// ── Токен ────────────────────────────────────────────────────────────────────

let token = null;
let tokenExpiresAt = 0;

/** Срок годности берём из самого JWT: приложение может поменять AccessTokenMinutes, и хардкод
 *  времени здесь разошёлся бы с реальностью. Не разбирается — считаем «час», как дефолт сервера. */
function expiryFromJwt(jwt) {
  try {
    const payload = JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString('utf8'));
    if (payload.exp) return payload.exp * 1000;
  } catch { /* не наше дело — просто откатимся к дефолту */ }
  return Date.now() + 60 * 60 * 1000;
}

async function login() {
  const res = await fetch(`${BASE}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: EMAIL, password: PASSWORD }),
  });
  if (!res.ok) {
    throw new Error(`вход не удался (${res.status}); проверьте BHS_EMAIL/BHS_PASSWORD и что приложение запущено на ${BASE}`);
  }
  const data = await res.json();
  if (!data.accessToken) throw new Error('ответ входа без accessToken');
  token = data.accessToken;
  tokenExpiresAt = expiryFromJwt(token);
  log(`вход выполнен, токен действителен до ${new Date(tokenExpiresAt).toLocaleTimeString()}`);
}

async function ensureToken() {
  if (!token || Date.now() > tokenExpiresAt - REFRESH_BEFORE_EXPIRY_MS) await login();
  return token;
}

// ── Транспорт к приложению ───────────────────────────────────────────────────

let sessionId = null;

async function post(message) {
  const send = async () => fetch(MCP_URL, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${await ensureToken()}`,
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
      ...(sessionId ? { 'Mcp-Session-Id': sessionId } : {}),
    },
    body: JSON.stringify(message),
  });

  let res = await send();
  // Токен мог быть отозван (смена пароля/секрета) до истечения срока — один повтор со свежим входом.
  if (res.status === 401) {
    log('получен 401 — вхожу заново');
    token = null;
    res = await send();
  }
  if (!sessionId) sessionId = res.headers.get('mcp-session-id') ?? null;
  return res;
}

/** Ответ приходит либо чистым JSON, либо потоком SSE (несколько сообщений за раз). */
function extractMessages(contentType, body) {
  if (!body.trim()) return [];
  if ((contentType ?? '').includes('text/event-stream')) {
    return body.split('\n')
      .filter(l => l.startsWith('data:'))
      .map(l => l.slice(5).trim())
      .filter(Boolean)
      .map(safeParse)
      .filter(Boolean);
  }
  const one = safeParse(body);
  return one ? [one] : [];
}

function safeParse(s) {
  try { return JSON.parse(s); } catch { log('не разобрал ответ приложения:', s.slice(0, 200)); return null; }
}

const write = obj => process.stdout.write(JSON.stringify(obj) + '\n');

// ── Цикл: stdin (клиент) → HTTP (приложение) → stdout (клиент) ────────────────

const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

/**
 * Сообщения обрабатываются СТРОГО последовательно, а не параллельно. Две причины:
 *  1. идентификатор сессии приходит с ответом на initialize — при параллельной отправке следующий
 *     запрос ушёл бы без него и получил отказ;
 *  2. предсказуемый порядок ответов важнее пропускной способности: здесь один агент читает данные,
 *     а не высоконагруженный трафик.
 * Цепочка промисов заодно даёт корректное завершение: на закрытии stdin ждём, пока долетят
 * незаконченные запросы, иначе процесс умрёт раньше ответа.
 */
let queue = Promise.resolve();

rl.on('line', line => { queue = queue.then(() => handle(line)); });
rl.on('close', () => {
  // Дожидаемся незаконченных запросов и НЕ зовём process.exit(): явный выход при закрытии stdin
  // роняет внутреннюю проверку libuv на Windows (проверено — падало на UV_HANDLE_CLOSING).
  // Цикл событий опустевает сам, процесс завершается сразу и с нулевым кодом.
  queue.finally(() => { process.exitCode = 0; });
});

async function handle(line) {
  const text = line.trim();
  if (!text) return;

  const request = safeParse(text);
  if (!request) return;

  try {
    const res = await post(request);
    const body = await res.text();

    if (!res.ok) {
      log(`приложение ответило ${res.status}: ${body.slice(0, 200)}`);
      // Уведомление (без id) ответа не ждёт — промолчать правильнее, чем слать мусор.
      if (request.id !== undefined) {
        write({
          jsonrpc: '2.0',
          id: request.id,
          error: { code: -32603, message: `BHS.CRG вернул ${res.status}: ${body.slice(0, 300)}` },
        });
      }
      return;
    }

    for (const msg of extractMessages(res.headers.get('content-type'), body)) write(msg);
  } catch (e) {
    log('ошибка обращения к приложению:', e.message);
    if (request.id !== undefined) {
      write({ jsonrpc: '2.0', id: request.id, error: { code: -32603, message: e.message } });
    }
  }
}

log(`мост запущен: ${MCP_URL} (пользователь ${EMAIL})`);
