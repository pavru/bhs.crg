#!/usr/bin/env node
// Снимок поверхности встроенных объектов на границе браузеров просмотрщика ([UI-21], change
// infra-node26, D6). Граница — Chrome/Firefox 126, Safari 18 (D5 viewer-00-epic); снимается она с
// Node 22 (V8 12.4 — не новее V8 12.6 у Chrome 126, то есть строже границы, а не шире).
//
// Пишет src/shared/testing/browserBorder.json: собственные ключи встроенных объектов ES. Тест
// движка pdf.js (pdfEngine.test.ts через browserBorder.ts) удаляет из среды всё, чего в снимке нет,
// и сторож заплаток работает на любой версии Node, а не только на старой. Веб-API (Response, Blob)
// и то, чего у Firefox 126 и Safari 18 нет при том, что есть у Node 22, снимок не моделирует.
//
// Снимок — факт о границе, а не о коде: переснимать, только когда владелец сдвинул границу.
// Известная неточность: `Symbol.dispose` и `Symbol.asyncDispose` Node 22 ставит сам, и в снимке они
// есть, хотя у Chrome 126 их нет. Так было и у сторожа, гонявшегося на Node 22.
//
//   MSYS_NO_PATHCONV=1 docker run --rm -v "$PWD/src/client:/w" -w /w node:22.23.3-alpine \
//     node scripts/browser-border.mjs

import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
// Node 22 читает .ts сам (снятие типов): разбор пути и ключа — один с помощником теста.
import { borderKeyName, resolveBorderPath } from '../src/shared/testing/browserBorderPaths.ts';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'shared', 'testing', 'browserBorder.json');

if (!process.versions.node.startsWith('22.')) {
  console.error(`Снимок снимается на Node 22, а это ${process.versions.node} — см. шапку файла.`);
  process.exit(1);
}

const CONSTRUCTORS = [
  'Object', 'Function', 'Array', 'String', 'Number', 'Boolean', 'Symbol', 'BigInt', 'Date', 'RegExp',
  'Error', 'AggregateError', 'TypeError', 'RangeError', 'SyntaxError',
  'Promise', 'Map', 'Set', 'WeakMap', 'WeakSet', 'WeakRef', 'FinalizationRegistry',
  'ArrayBuffer', 'SharedArrayBuffer', 'DataView', '%TypedArray%',
  'Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array',
  'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array', 'Iterator',
];
const NAMESPACES = ['Math', 'JSON', 'Reflect', 'Atomics', 'Intl'];

/** Ключи объекта; у глобального — только встроенные (неперечисляемые). */
function keys(target, onlyBuiltIn) {
  return Reflect.ownKeys(target)
    .filter(key => !onlyBuiltIn || !Object.getOwnPropertyDescriptor(target, key).enumerable)
    .map(borderKeyName)
    .filter(key => key !== undefined)
    .sort();
}

const objects = { globalThis: keys(globalThis, true) };
for (const name of [...CONSTRUCTORS.flatMap(c => [c, `${c}.prototype`]), ...NAMESPACES]) {
  if (resolveBorderPath(name) === undefined) throw new Error(`На Node 22 нет ${name}`);
  objects[name] = keys(resolveBorderPath(name), false);
}

writeFileSync(out, `${JSON.stringify({ node: process.versions.node, objects }, null, 1)}\n`);
console.log(`Записано: ${out}`);
