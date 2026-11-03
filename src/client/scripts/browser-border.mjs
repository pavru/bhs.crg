#!/usr/bin/env node
// Снимок поверхности встроенных объектов на границе браузеров просмотрщика ([UI-21], change
// infra-node26, D6; по самому слабому браузеру — change sharedui-pdf-border-gaps, D3). Граница —
// Chrome/Firefox 126, Safari 18 (D5 viewer-00-epic).
//
// Пишет src/shared/testing/browserBorder.json: собственные ключи встроенных объектов ES и веб-API,
// которые зовёт pdf.js, — снятые с Node 22 и без всего, чего по данным совместимости MDN
// (@mdn/browser-compat-data) нет ХОТЯ БЫ У ОДНОГО браузера границы: Node 22 по набору функций —
// Chrome, а у Firefox 126 и Safari 18 нет, например, итераторных помощников и глобала `Iterator`.
// Тест движка pdf.js (pdfEngine.test.ts через browserBorder.ts) удаляет из среды всё, чего в снимке
// нет, и сторож заплаток работает на любой версии Node.
//
// Ключа нет в данных MDN — он остаётся (служебное Node вроде `Buffer`): генератор печатает такие,
// пересъём читается глазами. Оставленное нарочно, хоть его на границе и нет, — KEPT, с причиной.
// DOM (холст, шрифты) снимок не моделирует вовсе.
//
// Снимок — факт о границе, а не о коде: переснимать, только когда владелец сдвинул границу или
// заплатки потребовали свежих данных MDN. Данные — не зависимость проекта: их ставит сама команда.
//
//   MSYS_NO_PATHCONV=1 docker run --rm -v "$PWD/src/client:/w" -w /w node:22.23.3-alpine sh -c \
//     'npm i -s --prefix /tmp/bcd @mdn/browser-compat-data@8.1.8 && node scripts/browser-border.mjs --bcd /tmp/bcd/node_modules/@mdn/browser-compat-data/data.json'

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
// Node 22 читает .ts сам (снятие типов): разбор пути и ключа — один с помощником теста.
import { borderKeyName, resolveBorderPath } from '../src/shared/testing/browserBorderPaths.ts';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'shared', 'testing', 'browserBorder.json');

if (!process.versions.node.startsWith('22.')) {
  console.error(`Снимок снимается на Node 22, а это ${process.versions.node} — см. шапку файла.`);
  process.exit(1);
}
const bcdAt = process.argv.indexOf('--bcd');
if (bcdAt < 0 || !process.argv[bcdAt + 1]) {
  console.error('Нужен путь к data.json из @mdn/browser-compat-data: --bcd <путь> (команда — в шапке файла).');
  process.exit(1);
}
const bcd = JSON.parse(readFileSync(process.argv[bcdAt + 1], 'utf8'));

const BROWSERS = { chrome: '126', firefox: '126', safari: '18' };

/** Нет на границе, но остаётся — среде тестов без этого не обойтись. Тест движка сверяет, что pdf.js их не зовёт. */
const KEPT = {
  'Error.captureStackTrace': 'среда тестов: стек ошибок vitest и Node (у Firefox нет)',
  'Error.stackTraceLimit': 'среда тестов: без него V8 не пишет стек, и место падения теряется (у Firefox нет)',
};

const BUILTINS = [
  'Object', 'Function', 'Array', 'String', 'Number', 'Boolean', 'Symbol', 'BigInt', 'Date', 'RegExp',
  'Error', 'AggregateError', 'TypeError', 'RangeError', 'SyntaxError',
  'Promise', 'Map', 'Set', 'WeakMap', 'WeakSet', 'WeakRef', 'FinalizationRegistry',
  'ArrayBuffer', 'SharedArrayBuffer', 'DataView', '%TypedArray%',
  'Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array',
  'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array',
];
const NAMESPACES = ['Math', 'JSON', 'Reflect', 'Atomics', 'Intl'];
/** Веб-API, которые зовёт сборка pdf.js. */
const WEB = [
  'AbortSignal', 'Blob', 'CompressionStream', 'DecompressionStream', 'ReadableStream', 'Response',
  'TextDecoder', 'TextEncoder', 'TransformStream', 'URL', 'WritableStream',
];

const compareVersions = (a, b) => {
  const x = a.split('.').map(Number), y = b.split('.').map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) - (y[i] ?? 0);
  return 0;
};

/** Поддержано на версии `at`: запись без флагов, префикса, другого имени и частичной реализации. */
function supportedAt(entries, at) {
  return [].concat(entries ?? []).some(e => {
    if (e.flags || e.prefix || e.alternative_name || e.partial_implementation) return false;
    const added = e.version_added;
    if (typeof added !== 'string' || added === 'preview' || compareVersions(added.replace('≤', ''), at) > 0) return false;
    return typeof e.version_removed !== 'string' || compareVersions(e.version_removed.replace('≤', ''), at) > 0;
  });
}

const node = parts => parts.reduce((n, k) => n?.[k], bcd);

/** Записи MDN, отвечающие ключу; первая найденная. */
function compatOf(path, key) {
  const k = key.startsWith('Symbol.') ? `@@${key.slice(7)}` : key;
  const [head, sub] = path.split('.');
  const candidates = path === 'globalThis'
    ? [['javascript', 'builtins', k, k], ['javascript', 'builtins', k], ['api', k]]
    : head === '%TypedArray%' ? [['javascript', 'builtins', 'TypedArray', k]]
    : head === '%IteratorPrototype%' ? [['javascript', 'builtins', 'Iterator', k]]
    : WEB.includes(head) ? (sub === 'prototype' ? [['api', head, k]] : [['api', head, `${k}_static`], ['api', head, k]])
    : [['javascript', 'builtins', head, k]];
  for (const c of candidates) {
    const support = node(c)?.__compat?.support;
    if (support) return { at: c.join('.'), support };
  }
  return undefined;
}

const dropped = [], unknown = [];
/** Ключи, которых в данных MDN нет у каждого объекта, — в печать «без данных» не идут. */
const TRIVIAL = new Set(['length', 'name', 'prototype', 'constructor', 'Symbol.toStringTag']);

/** Ключи объекта; у глобального — только встроенные (неперечисляемые). */
function keys(path, target) {
  const onlyBuiltIn = path === 'globalThis';
  const result = [];
  for (const key of Reflect.ownKeys(target)) {
    if (onlyBuiltIn && Object.getOwnPropertyDescriptor(target, key).enumerable) continue;
    const name = borderKeyName(key);
    if (name === undefined) continue;
    const compat = compatOf(path, name);
    const missing = compat && Object.entries(BROWSERS).filter(([b, at]) => !supportedAt(compat.support[b], at)).map(([b]) => b);
    const full = `${path}.${name}`;
    if (!compat) { if (!TRIVIAL.has(name)) unknown.push(full); }
    else if (missing.length > 0 && !(full in KEPT)) { dropped.push(`${full} — нет у ${missing.join(', ')} (${compat.at})`); continue; }
    else if (missing.length === 0 && full in KEPT) throw new Error(`${full} есть на границе — уберите его из KEPT`);
    result.push(name);
  }
  return result.sort();
}

const paths = ['globalThis', ...[...BUILTINS, ...WEB].flatMap(c => [c, `${c}.prototype`]), '%IteratorPrototype%', ...NAMESPACES];
const objects = {};
for (const path of paths) {
  const target = resolveBorderPath(path);
  if (target === undefined) throw new Error(`На Node 22 нет ${path}`);
  objects[path] = keys(path, target);
}
for (const kept of Object.keys(KEPT)) {
  const [path, name] = [kept.slice(0, kept.lastIndexOf('.')), kept.slice(kept.lastIndexOf('.') + 1)];
  if (!objects[path]?.includes(name)) throw new Error(`${kept} нет на Node 22 — уберите его из KEPT`);
}

const snapshot = { node: process.versions.node, bcd: bcd.__meta.version, browsers: BROWSERS, kept: KEPT, objects };
writeFileSync(out, `${JSON.stringify(snapshot, null, 1)}\n`);
console.log(`Выброшено по данным MDN ${bcd.__meta.version} (${dropped.length}):\n  ${dropped.join('\n  ')}`);
console.log(`Оставлено без данных MDN (${unknown.length}):\n  ${unknown.join('\n  ')}`);
console.log(`Записано: ${out}`);
